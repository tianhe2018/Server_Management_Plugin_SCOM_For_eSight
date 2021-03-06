﻿// ***********************************************************************
// Assembly         : Huawei.SCOM.ESightPlugin.RESTeSightLib
// Author           : yayun
// Created          : 12-25-2017
//
// Last Modified By : yayun
// Last Modified On : 12-25-2017
// ***********************************************************************
// <copyright file="ESightSyncInstance.cs" company="广州摩赛网络技术有限公司">
//     Copyright ©  2017
// </copyright>
// <summary></summary>
// ***********************************************************************

namespace Huawei.SCOM.ESightPlugin.Service
{
    using System;
    using System.Collections.Generic;
    using System.Management.Instrumentation;
    using System.Threading;
    using System.Threading.Tasks;

    using CommonUtil;

    using Huawei.SCOM.ESightPlugin.Core;
    using Huawei.SCOM.ESightPlugin.Core.Models;
    using Huawei.SCOM.ESightPlugin.Models;
    using Huawei.SCOM.ESightPlugin.Models.Devices;
    using Huawei.SCOM.ESightPlugin.Models.Server;
    using Huawei.SCOM.ESightPlugin.RESTeSightLib;
    using Huawei.SCOM.ESightPlugin.RESTeSightLib.Helper;

    using LogUtil;

    using Timer = System.Timers.Timer;

    /// <summary>
    ///     Class ESightSyncInstance.
    /// </summary>
    public partial class ESightSyncInstance
    {
        #region field
        /// <summary>
        /// 为保证线程安全，使用一个锁来保护_task的访问
        /// </summary>
        private readonly object locker = new object();

        /// <summary>
        /// The _wh.
        /// </summary>
        private readonly EventWaitHandle waitHandle = new AutoResetEvent(false);

        /// <summary>
        ///     The task list.
        /// </summary>
        private readonly List<Task> taskList = new List<Task>();
        #endregion

        #region Constuctor
        /// <summary>
        /// Initializes a new instance of the <see cref="ESightSyncInstance"/> class.
        /// </summary>
        /// <param name="eSight">
        /// The e sight.
        /// </param>
        public ESightSyncInstance(HWESightHost eSight)
        {
            this.Session = new ESightSession(eSight);
            this.NeedUpdateDnQueue = new Queue<string>();
            var worker = new Thread(this.DoJob);
            worker.Start();
        }
        #endregion

        #region Property

        /// <summary>
        /// Gets or sets the session.
        /// </summary>
        /// <value>The session.</value>
        public ESightSession Session { get; set; }

        /// <summary>
        ///     The is complete.
        /// </summary>
        /// <value><c>true</c> if this instance is complete; otherwise, <c>false</c>.</value>
        public bool IsComplete
        {
            get
            {
                foreach (var task in this.taskList)
                {
                    if (task.IsCompleted || task.IsCanceled || task.IsFaulted)
                    {
                        continue;
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Gets the e sight ip.
        /// </summary>
        /// <value>The e sight ip.</value>
        public string ESightIp => this.Session.ESight.HostIP;

        /// <summary>
        /// Gets the need subscribe alarm.
        /// </summary>
        /// <value>The need subscribe alarm.</value>
        public bool NeedSubscribeAlarm => this.Session.ESight.SubscriptionAlarmStatus != 1;

        /// <summary>
        /// Gets the need subscribe device change.
        /// </summary>
        /// <value>The need subscribe device change.</value>
        public bool NeedSubscribeDeviceChange => this.Session.ESight.SubscriptionNeDeviceStatus != 1;

        /// <summary>
        /// Gets or sets the dn queue.
        /// </summary>
        public Queue<string> NeedUpdateDnQueue { get; set; }

        #endregion

        /// <summary>
        /// Updates the e sight.
        /// </summary>
        /// <param name="eSight">The e sight.</param>
        public void UpdateESight(HWESightHost eSight)
        {
            this.Session.ESight = eSight;
        }

        /// <summary>
        /// Subscribes the eSight.
        /// </summary>
        public void Subscribe()
        {
            this.OnLog($"Start Subscribe");
            // 如果没有订阅，则订阅
            Task.Run(async () =>
            {
                await Task.Delay(3 * 1000);
                try
                {
                    if (this.NeedSubscribeAlarm)
                    {
                        var result = this.Session.SubscribeAlarm();

                        var status = 0;
                        if (result.Code != 0)
                        {
                            status = -1;
                            this.OnNotifyError($"Subscription Alarm faild.");
                            this.OnNotifyError($"{JsonUtil.SerializeObject(result)}");
                        }
                        else
                        {
                            status = 1;
                            this.OnNotifyLog($"Subscription Alarm Success:{JsonUtil.SerializeObject(result)}");
                        }
                        this.Session.ESight.SubscriptionAlarmStatus = status;
                        this.Session.ESight.SubscripeAlarmError = result.Description;
                        // 订阅后更新实体
                        ESightDal.Instance.UpdateESightAlarm(this.ESightIp, status, result.Description);
                    }
                }
                catch (Exception e)
                {
                    this.OnError($"Subscription Alarm error: {this.ESightIp}", e);
                }
                try
                {
                    if (this.NeedSubscribeDeviceChange)
                    {
                        var result = this.Session.SubscribeNeDevice();
                        var status = 0;
                        if (result.Code != 0)
                        {
                            status = -1;
                            this.OnNotifyError($"Subscription DeviceChange faild.");
                            this.OnNotifyError($"{JsonUtil.SerializeObject(result)}");
                        }
                        else
                        {
                            status = 1;
                            this.OnNotifyLog($"Subscription DeviceChange Success:{JsonUtil.SerializeObject(result)}");
                        }
                        this.Session.ESight.SubscriptionNeDeviceStatus = status;
                        this.Session.ESight.SubscripeNeDeviceError = result.Description;
                        // 订阅后更新实体
                        ESightDal.Instance.UpdateESightNeDevice(this.ESightIp, status, result.Description);
                    }
                }
                catch (Exception e)
                {
                    this.OnError($"Subscription DeviceChange error: {this.ESightIp}", e);
                }
            });
        }

        /// <summary>
        /// Synchronizes this instance.
        /// </summary>
        public void Sync()
        {
            if (!this.IsComplete)
            {
                this.OnLog("The last task was not completed, and the task was given up.");
                return;
            }
            if (!this.Session.IsCanConnect())
            {
                this.OnError($"Can not connect the esight {this.ESightIp}, and the task was given up.");
                return;
            }
            // 清除完成的任务
            this.taskList.Clear();
            this.SyncBladeList();
            this.SyncHighdensityList();
            this.SyncRackList();
            this.SyncKunLunList();
            if (this.NeedSubscribeAlarm || this.NeedSubscribeDeviceChange)
            {
                this.OnLog("Need Subscribe");
                // 开启一个timer来监听本次任务是否执行完
                Timer timer = new Timer(1000);
                timer.Elapsed += (sender, e) =>
                {
                    if (this.IsComplete)
                    {
                        this.Subscribe();
                        timer.Stop();
                    }
                };
                timer.Start();
            }
        }

        /// <summary>
        /// The do job.
        /// </summary>
        public void DoJob()
        {
            while (this.NeedUpdateDnQueue.Count > 0 || this.waitHandle.WaitOne())
            {
                lock (this.locker)
                {
                    if (this.NeedUpdateDnQueue.Count > 0)
                    {
                        try
                        {
                            var dn = this.NeedUpdateDnQueue.Dequeue(); // 有任务时，出列任务
                            HWLogger.NOTIFICATION_PROCESS.Debug($"eSight-{this.ESightIp} Dequeue:{dn}  Queue Count:{ this.NeedUpdateDnQueue.Count}");
                            if (dn == null)
                            {
                                continue;
                            }
                            HWLogger.NOTIFICATION_PROCESS.Info($"eSight-{this.ESightIp} Start UpdateServer " + dn);
                            this.UpdateServer(dn);
                            HWLogger.NOTIFICATION_PROCESS.Info($"eSight-{this.ESightIp} End UpdateServer " + dn);
                        }
                        catch (Exception ex)
                        {
                            HWLogger.NOTIFICATION_PROCESS.Error("DoJob Error", ex);
                        }
                    }
                }
                Thread.Sleep(300);
            }
        }

        /// <summary>
        /// Enqueues the specified model.
        /// </summary>
        /// <param name="dn">The dn.</param>
        public void Enqueue(string dn)
        {
            HWLogger.NOTIFICATION_PROCESS.Debug($"eSight-{this.ESightIp} Enqueue: {dn} ");
            string item = $"{dn}";
            if (!this.NeedUpdateDnQueue.Contains(item))
            {
                this.NeedUpdateDnQueue.Enqueue(item);
                this.waitHandle.Set();
            }
            else
            {
                HWLogger.NOTIFICATION_PROCESS.Debug($"eSight-{this.ESightIp} Cancel Enqueue:" + item);
            }
        }

        #region 刀片

        /// <summary>
        /// 同步刀片服务器
        /// </summary>
        public void SyncBladeList()
        {
            var task = Task.Run(() =>
                {
                    int totalPage = 1;
                    int startPage = 0;
                    while (startPage < totalPage)
                    {
                        try
                        {
                            startPage++;
                            var result = this.Session.QueryBladeServer(startPage);
                            totalPage = result.TotalPage;
                            foreach (var x in result.Data)
                            {
                                x.ESight = this.ESightIp;
                                this.QueryBladeDetial(x);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.OnError($"SyncBladeList Error.eSight:{this.ESightIp} ", ex);
                        }
                    }
                });
            this.taskList.Add(task);
        }

        /// <summary>
        /// Queries the blade detial.
        /// </summary>
        /// <param name="x">The x.</param>
        public void QueryBladeDetial(BladeServer x)
        {
            var task = Task.Run(() =>
                {
                    try
                    {
                        var device = this.Session.GetServerDetails(x.DN);
                        x.MakeDetail(device);
                        x.ChildBlades.ForEach(m =>
                        {
                            var deviceDatail = this.Session.GetServerDetails(m.DN);
                            m.MakeChildBladeDetail(deviceDatail);
                        });

                        // 插入數據
                        BladeConnector.Instance.InsertDetials(x);
                    }
                    catch (Exception ex)
                    {
                        this.OnError($"QueryBladeDetial Error:DN:{x.DN}", ex);
                    }
                });
            this.taskList.Add(task);
        }
        #endregion

        #region 高密

        /// <summary>
        /// 同步高密服务器的详情
        /// </summary>
        public void SyncHighdensityList()
        {
            var task = Task.Run(() =>
                {
                    int totalPage = 1;
                    int startPage = 0;
                    while (startPage < totalPage)
                    {
                        try
                        {
                            startPage++;
                            var result = this.Session.QueryHighDesentyServer(startPage);
                            totalPage = result.TotalPage;
                            foreach (var x in result.Data)
                            {
                                x.ESight = this.ESightIp;
                                this.QueryHighdensityDetial(x);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.OnError($"SyncHighdensityList Error.eSight:{this.ESightIp} ", ex);
                        }
                    }
                });
            this.taskList.Add(task);
        }

        /// <summary>
        /// Queries the highdensity detial.
        /// </summary>
        /// <param name="x">The x.</param>
        public void QueryHighdensityDetial(HighdensityServer x)
        {
            var task = Task.Run(() =>
                {
                    try
                    {
                        var device = this.Session.GetServerDetails(x.DN);
                        x.MakeDetail(device);
                        x.ChildHighdensitys.ForEach(
                            m =>
                                {
                                    var deviceDatail = this.Session.GetServerDetails(m.DN);
                                    m.MakeChildBladeDetail(deviceDatail);
                                });

                        // 插入數據
                        HighdensityConnector.Instance.InsertDetials(x);
                    }
                    catch (Exception ex)
                    {
                        this.OnError($"QueryHighdensityDetial Error:DN:{x.DN}", ex);
                    }
                });
            this.taskList.Add(task);
        }
        #endregion

        #region 机架

        /// <summary>
        /// 同步机架服务器的详情
        /// </summary>
        public void SyncRackList()
        {
            var task = Task.Run(() =>
            {
                int totalPage = 1;
                int startPage = 0;
                while (startPage < totalPage)
                {
                    try
                    {
                        startPage++;
                        var result = this.Session.QueryRackServer(startPage);
                        totalPage = result.TotalPage;
                        foreach (var x in result.Data)
                        {
                            this.QueryRackDetial(x);
                        }
                    }
                    catch (Exception ex)
                    {
                        this.OnError($"SyncRackList Error.eSight:{this.ESightIp} ", ex);
                    }
                }
            });
            this.taskList.Add(task);
        }

        /// <summary>
        /// Queries the rack detial.
        /// </summary>
        /// <param name="rack">The rack.</param>
        public void QueryRackDetial(RackServer rack)
        {
            var task = Task.Run(() =>
                {
                    try
                    {
                        var device = this.Session.GetServerDetails(rack.DN);
                        rack.ESight = this.ESightIp;
                        rack.MakeDetail(device);
                        RackConnector.Instance.InsertDetials(rack);
                    }
                    catch (Exception ex)
                    {
                        this.OnError($"QueryRackDetial Error: {rack.DN}", ex);
                    }
                });
            this.taskList.Add(task);
        }
        #endregion

        #region 昆仑

        /// <summary>
        /// 同步昆仑服务器的详情
        /// </summary>
        public void SyncKunLunList()
        {
            var task = Task.Run(() =>
                {
                    int totalPage = 1;
                    int startPage = 0;
                    while (startPage < totalPage)
                    {
                        try
                        {
                            startPage++;
                            var result = this.Session.QueryKunLunServer(startPage);
                            totalPage = result.TotalPage;
                            foreach (var x in result.Data)
                            {
                                this.QueryKunLunDetial(x);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.OnError($"SyncKunLunList Error.eSight:{this.ESightIp} ", ex);
                        }
                    }
                });
            this.taskList.Add(task);
        }

        /// <summary>
        /// Queries the kunLun detial.
        /// </summary>
        /// <param name="kunLun">The kun lun.</param>
        public void QueryKunLunDetial(KunLunServer kunLun)
        {
            var task = Task.Run(() =>
                {
                    try
                    {
                        var device = this.Session.GetServerDetails(kunLun.DN);
                        kunLun.ESight = this.ESightIp;
                        kunLun.MakeDetail(device);
                        KunLunConnector.Instance.InsertDetials(kunLun);
                    }
                    catch (Exception ex)
                    {
                        this.OnError($"QueryKunLunDetial Error:{kunLun.DN}", ex);
                    }
                });
            this.taskList.Add(task);
        }
        #endregion

        #region Notify
        /// <summary>
        /// The delete server.
        /// </summary>
        /// <param name="dn">The dn.</param>
        public void DeleteServer(string dn)
        {
            Task.Run(() =>
            {
                try
                {
                    var serverType = this.GetServerType(dn);
                    switch (serverType)
                    {
                        case ServerTypeEnum.Blade:
                            BladeConnector.Instance.RemoveComputerByDn(dn);
                            break;
                        case ServerTypeEnum.ChildBlade:
                            BladeConnector.Instance.RemoveChildBlade(dn);
                            break;
                        case ServerTypeEnum.Highdensity:
                            HighdensityConnector.Instance.RemoveComputerByDn(dn);
                            break;
                        case ServerTypeEnum.ChildHighdensity:
                            HighdensityConnector.Instance.RemoveChildHighDensityServer(dn);
                            break;
                        case ServerTypeEnum.Rack:
                            RackConnector.Instance.RemoveComputerByDn(dn);
                            break;
                        case ServerTypeEnum.KunLun:
                            KunLunConnector.Instance.RemoveComputerByDn(dn);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    HWLogger.UI.Error($"DeleteServer {dn} Error: ", ex);
                }
            });
        }

        /// <summary>
        /// The get server type.
        /// </summary>
        /// <param name="dn">
        /// The dn.
        /// </param>
        /// <returns>
        /// The <see cref="ServerTypeEnum"/>.
        /// </returns>
        public ServerTypeEnum GetServerType(string dn)
        {
            HWLogger.NOTIFICATION_PROCESS.Debug($"Start GetServerType {dn}");
            var server = BladeConnector.Instance.GetBladeServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.Blade;
            }
            server = BladeConnector.Instance.GetChildBladeServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.ChildBlade;
            }
            server = HighdensityConnector.Instance.GetHighdensityServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.Highdensity;
            }
            server = HighdensityConnector.Instance.GetChildHighdensityServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.ChildHighdensity;
            }
            server = RackConnector.Instance.GetRackServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.Rack;
            }
            server = KunLunConnector.Instance.GetKunLunServer(dn);
            if (server != null)
            {
                return ServerTypeEnum.KunLun;
            }
            HWLogger.NOTIFICATION_PROCESS.Debug($"End GetServerType {dn}");
            return ServerTypeEnum.Default;
        }

        /// <summary>
        /// Inserts the device change event.
        /// </summary>
        /// <param name="data">The data.</param>
        public void InsertDeviceChangeEvent(NedeviceData data)
        {
            Task.Run(() =>
            {
                try
                {
                    var serverType = this.GetServerType(data.DeviceId);

                    HWLogger.NOTIFICATION_PROCESS.Info($"Start deviceChangeEvent :{data.DeviceId}");
                    var deviceChangeEventData = new DeviceChangeEventData(data);
                    switch (serverType)
                    {
                        case ServerTypeEnum.Blade:
                            BladeConnector.Instance.InsertDeviceChangeEvent(deviceChangeEventData);
                            break;
                        case ServerTypeEnum.ChildBlade:
                            BladeConnector.Instance.InsertChildDeviceChangeEvent(deviceChangeEventData);
                            break;
                        case ServerTypeEnum.Highdensity:
                            HighdensityConnector.Instance.InsertDeviceChangeEvent(deviceChangeEventData);
                            break;
                        case ServerTypeEnum.ChildHighdensity:
                            HighdensityConnector.Instance.InsertChildDeviceChangeEvent(deviceChangeEventData);
                            break;
                        case ServerTypeEnum.Rack:
                            RackConnector.Instance.InsertDeviceChangeEvent(deviceChangeEventData);
                            break;
                        case ServerTypeEnum.KunLun:
                            KunLunConnector.Instance.InsertDeviceChangeEvent(deviceChangeEventData);
                            break;
                    }
                    HWLogger.NOTIFICATION_PROCESS.Info($"End deviceChangeEvent: {data.DeviceId}");
                }
                catch (Exception ex)
                {
                    HWLogger.UI.Error("InsertEvent Error: ", ex);
                }
            });
        }

        /// <summary>
        /// The insert event.
        /// </summary>
        /// <param name="alarmData">The alarm data.</param>
        public void InsertEvent(AlarmData alarmData)
        {
            Task.Run(() =>
            {
                try
                {
                    var serverType = this.GetServerType(alarmData.NeDN);

                    HWLogger.NOTIFICATION_PROCESS.Info($"Start InsertEvent {alarmData.NeDN}");
                    var alertModel = new EventData(alarmData);

                    switch (serverType)
                    {
                        case ServerTypeEnum.Blade:
                            BladeConnector.Instance.InsertEvent(alertModel);
                            break;
                        case ServerTypeEnum.ChildBlade:
                            BladeConnector.Instance.InsertChildBladeEvent(alertModel);
                            break;
                        case ServerTypeEnum.Highdensity:
                            HighdensityConnector.Instance.InsertEvent(alertModel);
                            break;
                        case ServerTypeEnum.ChildHighdensity:
                            HighdensityConnector.Instance.InsertChildBladeEvent(alertModel);
                            break;
                        case ServerTypeEnum.Rack:
                            RackConnector.Instance.InsertEvent(alertModel);
                            break;
                        case ServerTypeEnum.KunLun:
                            KunLunConnector.Instance.InsertEvent(alertModel);
                            break;
                    }
                    HWLogger.NOTIFICATION_PROCESS.Info($"End InsertEvent {alarmData.NeDN}");
                }
                catch (Exception ex)
                {
                    HWLogger.UI.Error("InsertEvent Error: ", ex);
                }
            });
        }

        /// <summary>
        /// 更新刀片机架
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateBladeServer(HWDeviceDetail model)
        {
            try
            {
                var server = new BladeServer
                {
                    DN = model.DN,
                    ServerName = model.Name,
                    Manufacturer = string.Empty,
                    ServerModel = model.Mode,
                    IpAddress = model.IpAddress,
                    Location = string.Empty,
                    Status = model.Status
                };
                server.MakeDetail(model);
                server.ESight = this.ESightIp;
                BladeConnector.Instance.UpdateMainWithOutChildBlade(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateBladeServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// 更新子刀片
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateChildBladeServer(HWDeviceDetail model)
        {
            try
            {
                var server = new ChildBlade();
                server.MakeChildBladeDetail(model);
                server.DN = model.DN;
                server.Name = model.Name;
                server.IpAddress = model.IpAddress;
                BladeConnector.Instance.UpdateChildBlade(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateChildBladeServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// 更新高密子服务器
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateChildHighdensityServer(HWDeviceDetail model)
        {
            try
            {
                var server = new ChildHighdensity();
                server.DN = model.DN;
                server.Name = model.Name;
                server.IpAddress = model.IpAddress;
                server.MakeChildBladeDetail(model);
                HighdensityConnector.Instance.UpdateChildBoard(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateChildHighdensityServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// 更新高密管理板
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateHighdensityServer(HWDeviceDetail model)
        {
            try
            {
                var server = new HighdensityServer
                {
                    DN = model.DN,
                    ServerName = model.Name,
                    Manufacturer = string.Empty,
                    ServerModel = model.Mode,
                    IpAddress = model.IpAddress,
                    Location = string.Empty,
                    Status = model.Status,
                    ESight = this.ESightIp
                };
                server.MakeDetail(model);
                HighdensityConnector.Instance.UpdateMainWithOutChildBlade(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateHighdensityServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// 更新昆仑
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateKunLunServer(HWDeviceDetail model)
        {
            try
            {
                var server = new KunLunServer();
                server.ESight = this.ESightIp;
                server.MakeDetail(model);
                KunLunConnector.Instance.UpdateKunLun(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateKunLunServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// 更新机架
        /// </summary>
        /// <param name="model">The model.</param>
        public void UpdateRackServer(HWDeviceDetail model)
        {
            try
            {
                var server = new RackServer();
                server.MakeDetail(model);
                server.ESight = this.ESightIp;
                RackConnector.Instance.UpdateRack(server);
            }
            catch (Exception ex)
            {
                this.OnNotifyError($"UpdateRackServer Error.eSight:{this.ESightIp} Dn:{model.DN}. ", ex);
            }
        }

        /// <summary>
        /// The update server.
        /// </summary>
        /// <param name="dn">The dn.</param>
        public void UpdateServer(string dn)
        {
            var serverType = this.GetServerType(dn);
            try
            {
                var device = this.Session.GetServerDetails(dn);
                switch (serverType)
                {
                    case ServerTypeEnum.Blade:
                        this.UpdateBladeServer(device);
                        break;
                    case ServerTypeEnum.ChildBlade:
                        this.UpdateChildBladeServer(device);
                        break;
                    case ServerTypeEnum.Highdensity:
                        this.UpdateHighdensityServer(device);
                        break;
                    case ServerTypeEnum.ChildHighdensity:
                        this.UpdateChildHighdensityServer(device);
                        break;
                    case ServerTypeEnum.Rack:
                        this.UpdateRackServer(device);
                        break;
                    case ServerTypeEnum.KunLun:
                        this.UpdateKunLunServer(device);
                        break;
                }
            }
            catch (Exception ex)
            {
                HWLogger.NOTIFICATION_PROCESS.Error("UpdateServer Error: ", ex);
            }
        }
        #endregion

        #region Log
        /// <summary>
        /// The on error.
        /// </summary>
        /// <param name="msg">
        /// The msg.
        /// </param>
        /// <param name="ex">
        /// The ex.
        /// </param>
        private void OnError(string msg, Exception ex = null)
        {
            HWLogger.SERVICE.Error(msg, ex);
        }

        /// <summary>
        /// The on log.
        /// </summary>
        /// <param name="msg">
        /// The msg.
        /// </param>
        private void OnLog(string msg)
        {
            HWLogger.SERVICE.Info(msg);
        }

        /// <summary>
        /// The on error.
        /// </summary>
        /// <param name="msg">
        /// The msg.
        /// </param>
        /// <param name="ex">
        /// The ex.
        /// </param>
        private void OnNotifyError(string msg, Exception ex = null)
        {
            HWLogger.SERVICE.Error(msg, ex);
            HWLogger.NOTIFICATION_PROCESS.Error(msg, ex);
        }

        /// <summary>
        /// The on log.
        /// </summary>
        /// <param name="msg">
        /// The msg.
        /// </param>
        private void OnNotifyLog(string msg)
        {
            HWLogger.SERVICE.Info(msg);
            HWLogger.NOTIFICATION_PROCESS.Info(msg);
        }
        #endregion
    }
}
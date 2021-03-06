﻿// ***********************************************************************
// Assembly         : Huawei.SCOM.ESightPlugin.Core
// Author           : yayun
// Created          : 12-19-2017
//
// Last Modified By : yayun
// Last Modified On : 12-19-2017
// ***********************************************************************
// <copyright file="DeviceChangeEventData.cs" company="广州摩赛网络技术有限公司">
//     Copyright ©  2017
// </copyright>
// <summary>The device change event data.</summary>
// ***********************************************************************

namespace Huawei.SCOM.ESightPlugin.Core.Models
{
    using System;
    using CommonUtil;
    using Huawei.SCOM.ESightPlugin.Models;
    using Microsoft.EnterpriseManagement.Monitoring;

    /// <summary>
    /// The device change event data.
    /// </summary>
    public class DeviceChangeEventData
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceChangeEventData"/> class. 
        /// Initializes a new instance of the <see cref="EventData"/> class.
        /// </summary>
        /// <param name="data">
        /// The data.
        /// </param>
        public DeviceChangeEventData(NedeviceData data)
        {
            this.Dn = data.DeviceId;
            this.LoggingComputer = data.DeviceId;
            this.Message = data.Description;
            this.NedeviceData = data;
        }

        /// <summary>
        ///     Gets or sets the channel.
        /// </summary>
        /// <value>The channel.</value>
        public string Channel => "Device change notification";

        /// <summary>
        ///     Gets or sets the dn.
        /// </summary>
        /// <value>The dn.</value>
        public string Dn { get; set; }

        /// <summary>
        ///     Gets or sets the logging computer.
        /// </summary>
        /// <value>The logging computer.</value>
        public string LoggingComputer { get; set; }

        /// <summary>
        ///     Gets or sets the message.
        /// </summary>
        /// <value>The message.</value>
        public string Message { get; set; }

        /// <summary>
        ///     Gets or sets the nedevice data.
        /// </summary>
        /// <value>The nedevice data.</value>
        public NedeviceData NedeviceData { get; set; }

        /// <summary>
        ///  To the custom monitoring event.
        /// </summary>
        /// <returns>CustomMonitoringEvent.</returns>
        public CustomMonitoringEvent ToCustomMonitoringEvent()
        {
            return new CustomMonitoringEvent(this.Dn, 0)
            {
                LoggingComputer = this.LoggingComputer,
                Channel = this.Channel,
                TimeGenerated = DateTime.Now,

                // 默认为info
                LevelId = 4,
                EventData = this.ToEventData()
                // Message = new CustomMonitoringEventMessage(this.Message)
            };
        }

        /// <summary>
        ///  To the event data.
        /// </summary>
        /// <returns>System.String.</returns>
        private string ToEventData()
        {
            return XmlHelper.SerializeToXmlStr(this.NedeviceData, true);
        }
    }
}
﻿using P2PSocket.Client.Models.Send;
using P2PSocket.Client.Utils;
using P2PSocket.Core;
using P2PSocket.Core.Extends;
using P2PSocket.Core.Models;
using P2PSocket.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace P2PSocket.Client
{
    public class P2PClient
    {
        public Dictionary<string, TcpListener> ListenerList { set; get; } = new Dictionary<string, TcpListener>();
        public P2PClient()
        {

        }

        internal void ConnectServer()
        {
            try
            {
                Global.P2PServerTcp = new P2PTcpClient(Global.ServerAddress, Global.ServerPort);
            }
            catch
            {
                LogUtils.Error($"{DateTime.Now.ToString("HH:mm:ss")} 无法连接服务器:{Global.ServerAddress}:{Global.ServerPort}");
                return;
            }
            LogUtils.Info($"{DateTime.Now.ToString("HH:mm:ss")} 已连接服务器:{Global.ServerAddress}:{Global.ServerPort}", false);
            Global.P2PServerTcp.IsAuth = true;
            Global.TaskFactory.StartNew(() =>
            {
                //向服务器发送客户端信息
                InitServerInfo(Global.P2PServerTcp);
                //监听来自服务器的消息
                Global_Func.ListenTcp<ReceivePacket>(Global.P2PServerTcp);
            });
        }

        internal void TestAndReconnectServer()
        {
            Guid curGuid = Global.CurrentGuid;
            while (true)
            {
                Thread.Sleep(5000);
                if (curGuid != Global.CurrentGuid) break;
                if (Global.P2PServerTcp != null)
                {
                    try
                    {
                        Global.P2PServerTcp.Client.Send(new Send_0x0052().PackData());
                    }
                    catch (Exception ex)
                    {
                        LogUtils.Warning($"{DateTime.Now.ToString("HH:mm:ss")} 服务器连接已被断开");
                        Global.P2PServerTcp = null;
                    }
                }
                else
                {
                    ConnectServer();
                }
            }
        }

        /// <summary>
        ///     向服务器发送客户端信息
        /// </summary>
        /// <param name="tcpClient"></param>
        private void InitServerInfo(P2PTcpClient tcpClient)
        {
            Send_0x0101 sendPacket = new Send_0x0101();
            LogUtils.Info($"客户端名称：{Global.ClientName}");
            tcpClient.Client.Send(sendPacket.PackData());
        }


        /// <summary>
        ///     监听映射端口
        /// </summary>
        internal void StartPortMap()
        {
            if (Global.PortMapList.Count == 0) return;
            Dictionary<string, TcpListener> curListenerList = ListenerList;
            ListenerList = new Dictionary<string, TcpListener>();
            foreach (PortMapItem item in Global.PortMapList)
            {
                string key = $"{item.LocalAddress}:{item.LocalPort}";
                if (curListenerList.ContainsKey(key))
                {
                    LogUtils.Trace($"正在监听端口：{key}");
                    ListenerList.Add(key, curListenerList[key]);
                    curListenerList.Remove(key);
                    continue;
                }
                if (item.MapType == PortMapType.ip)
                {
                    ListenPortMapPortWithIp(item);
                }
                else
                {
                    ListenPortMapPortWithServerName(item);
                }
            }
            foreach(TcpListener listener in curListenerList.Values)
            {
                LogUtils.Trace($"停止端口监听：{listener.LocalEndpoint.ToString()}");
                listener.Stop();
            }
        }

        private void ListenPortMapPortWithServerName(PortMapItem item)
        {
            TcpListener listener = new TcpListener(string.IsNullOrEmpty(item.LocalAddress) ? IPAddress.Any : IPAddress.Parse(item.LocalAddress), item.LocalPort);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                LogUtils.Error($"端口映射失败：{item.LocalPort}->{item.RemoteAddress}:{item.RemotePort}{Environment.NewLine}{ex.Message}");
                return;
            }
            ListenerList.Add($"{item.LocalAddress}:{item.LocalPort}", listener);
            LogUtils.Info($"端口映射：{item.LocalPort}->{item.RemoteAddress}:{item.RemotePort}", false);
            Global.TaskFactory.StartNew(() =>
            {
                while (true)
                {
                    Socket socket = null;
                    try
                    {
                        socket = listener.AcceptSocket();
                    }
                    catch
                    {
                        break;
                    }
                    //获取目标tcp
                    if (Global.P2PServerTcp != null && Global.P2PServerTcp.Connected)
                    {
                        Global.TaskFactory.StartNew(() =>
                        {
                            P2PTcpClient tcpClient = new P2PTcpClient(socket);
                            //加入待连接集合
                            Global.WaiteConnetctTcp.Add(tcpClient.Token, tcpClient);
                            //发送p2p申请
                            Send_0x0201_Apply packet = new Send_0x0201_Apply(tcpClient.Token, item.RemoteAddress, item.RemotePort);
                            LogUtils.Info($"正在建立内网穿透（3端）通道 token:{tcpClient.Token} client:{item.RemoteAddress} port:{item.RemotePort}");
                            try
                            {
                                Global.P2PServerTcp.Client.Send(packet.PackData());
                            }
                            finally
                            {
                                //如果5秒后没有匹配成功，则关闭连接
                                Thread.Sleep(Global.P2PTimeout);
                                if (Global.WaiteConnetctTcp.ContainsKey(tcpClient.Token))
                                {
                                    LogUtils.Warning($"内网穿透失败：token:{tcpClient.Token} {item.LocalPort}->{item.RemoteAddress}:{item.RemotePort} {Global.P2PTimeout}秒无响应，已超时.");
                                    Global.WaiteConnetctTcp[tcpClient.Token].Close();
                                    Global.WaiteConnetctTcp.Remove(tcpClient.Token);
                                }

                            }
                        });
                    }
                    else
                    {
                        LogUtils.Warning($"内网穿透失败：未连接到服务器!");
                        socket.Close();
                    }
                }
            });
        }

        /// <summary>
        ///     直接转发类型的端口监听
        /// </summary>
        /// <param name="item"></param>
        private void ListenPortMapPortWithIp(PortMapItem item)
        {
            TcpListener listener = new TcpListener(string.IsNullOrEmpty(item.LocalAddress) ? IPAddress.Any : IPAddress.Parse(item.LocalAddress), item.LocalPort);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                LogUtils.Error($"添加端口映射失败：{item.LocalPort}->{item.RemoteAddress}:{item.RemotePort}{Environment.NewLine}{ex.ToString()}");
                return;
            }
            ListenerList.Add($"{item.LocalAddress}:{item.LocalPort}", listener);
            LogUtils.Info($"添加端口映射成功：{item.LocalPort}->{item.RemoteAddress}:{item.RemotePort}", false);
            Global.TaskFactory.StartNew(() =>
            {
                while (true)
                {
                    Socket socket = listener.AcceptSocket();
                    P2PTcpClient tcpClient = new P2PTcpClient(socket);
                    Global.TaskFactory.StartNew(() =>
                    {
                        P2PTcpClient ipClient = null;
                        try
                        {
                            ipClient = new P2PTcpClient(item.RemoteAddress, item.RemotePort);
                        }
                        catch (Exception ex)
                        {
                            tcpClient.Close();
                            LogUtils.Error($"内网穿透失败：{item.LocalPort}->{item.RemoteAddress}:{item.RemotePort}{Environment.NewLine}{ex}");
                        }
                        if (ipClient.Connected)
                        {
                            tcpClient.ToClient = ipClient;
                            ipClient.ToClient = tcpClient;
                            Global.TaskFactory.StartNew(() => ListenPortMapTcpWithIp(tcpClient));
                            Global.TaskFactory.StartNew(() => ListenPortMapTcpWithIp(tcpClient.ToClient));
                        }
                    });
                }
            });
        }

        /// <summary>
        ///     监听映射端口并转发数据（ip直接转发模式）
        /// </summary>
        /// <param name="readClient"></param>
        private void ListenPortMapTcpWithIp(P2PTcpClient readClient)
        {
            if (readClient.ToClient == null || !readClient.ToClient.Connected)
            {
                LogUtils.Warning($"数据转发（ip模式）失败：绑定的Tcp连接已断开.");
                readClient.Close();
                return;
            }
            byte[] buffer = new byte[P2PGlobal.P2PSocketBufferSize];
            try
            {
                NetworkStream readStream = readClient.GetStream();
                NetworkStream toStream = readClient.ToClient.GetStream();
                while (readClient.Connected)
                {
                    int curReadLength = readStream.ReadSafe(buffer, 0, buffer.Length);
                    if (curReadLength > 0)
                    {
                        toStream.Write(buffer, 0, curReadLength);
                    }
                    else
                    {
                        LogUtils.Warning($"数据转发（ip模式）失败：Tcp连接已断开.");
                        //如果tcp已关闭，需要关闭相关tcp
                        try
                        {
                            readClient.ToClient.Close();
                        }
                        finally
                        {
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtils.Warning($"【失败】IP数据转发：目标Tcp连接已断开.");
                readClient.Close();
            }
        }
    }
}

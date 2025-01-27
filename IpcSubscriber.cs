using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static MemoryMappedFileIPC.IpcUtils;

namespace MemoryMappedFileIPC
{
    
    /// <summary>
    /// One server has multiple IPCServerConnections to allow for more than one client
    /// It creates more connections as needed
    /// </summary>
    public class IpcSubscriber : IDisposable
    {
        public string serverDirectory;
        public string channelName;
        public int millisBetweenPing;
        int processId;
        
        CancellationTokenSource stopToken = new CancellationTokenSource();
        
        volatile ConcurrentBag<IpcServerConnection> connections = new ConcurrentBag<IpcServerConnection>();

        public event IpcServerConnection.RecievedBytesCallback RecievedBytes;
        
        
        public ManualResetEvent connectEvent = new ManualResetEvent(false);
        public ManualResetEvent disconnectEvent = new ManualResetEvent(true);
        object connectEventLock = new object();

        DebugLogType DebugLog;

        public IpcSubscriber(string channelName, string serverDirectory, int millisBetweenPing=1000, DebugLogType logger=null) {
            this.DebugLog = logger;
            if (DebugLog == null)
            {
                this.DebugLog = (x) => { };
            }
            this.channelName = channelName;
            this.serverDirectory = serverDirectory;
            Directory.CreateDirectory(serverDirectory); // create if not exist
            this.millisBetweenPing = millisBetweenPing;
            this.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            AddNewListener();
        }

        void AddNewListener()
        {
            DebugLog("Adding new ipc listener");
            IpcServerConnection connection = new IpcServerConnection(
                this.channelName,
                this.serverDirectory,
                this.millisBetweenPing,
                this.processId,
                stopToken,
                DebugLog);
            connections.Add(connection);
            connection.OnDisconnect += () =>
            {
                DebugLog("ipc listener " + connection.GetServerKey() + " disconnected");
                UpdateConnectionEvents();
                if (!stopToken.IsCancellationRequested)
                {
                    CheckListeners();
                }
            };
            connection.OnConnect += () =>
            {
                DebugLog("ipc listener " + connection.GetServerKey() + " connected");
                UpdateConnectionEvents();
                
                if (!stopToken.IsCancellationRequested)
                {
                    CheckListeners();
                }
            };
            // once it connects, we need to open a new channel for new people that want to connect
            connection.OnRecievedBytes += (byte[] bytes) =>
            {
                DebugLog("Recieved bytes in handler " + bytes.Length);
                this.RecievedBytes?.Invoke(bytes);
            };
            connection.Init();
        }

        void UpdateConnectionEvents()
        {
            // we need to lock to ensure we don't set this between the IsConnected check above and resetting
            lock (connectEventLock)
            {
                if (NumActiveConnections() > 0)
                {
                    connectEvent.Set();
                    disconnectEvent.Reset();
                }
                else
                {
                    connectEvent.Reset();
                    disconnectEvent.Set();
                }
            }
        }

        void CheckListeners()
        {
            // if no one is waiting for connection, make a new one (maybe an error was thrown in connection attempt)
            bool anyAreWaiting = false;
            lock(connectEventLock)
            {
                anyAreWaiting = connections.Any(
                    (c) =>
                        c.connectionStatus == IpcUtils.ConnectionStatus.WaitingForConnection);
            }
            if (!anyAreWaiting)
            {
                AddNewListener();
            }
            
            // remove all terminated connections from bag and dispose them
            lock(connectEventLock)
            {
                List<IpcServerConnection> terminatedConnections = new List<IpcServerConnection>();
                ConcurrentBag<IpcServerConnection> cleanedBag = new ConcurrentBag<IpcServerConnection>();
                foreach (IpcServerConnection connection in connections)
                {
                    if (connection.connectionStatus == ConnectionStatus.Terminated)
                    {
                        terminatedConnections.Add(connection);
                    }
                    else
                    {
                        cleanedBag.Add(connection);
                    }
                }
                connections = cleanedBag;
                // need to terminate in seperate thread since we may be currently executing
                // within one of the threads that we need to join on dispose
                if (terminatedConnections.Count > 0)
                {
                    new Thread(() =>
                    {
                        // dispose all terminated connections
                        foreach (IpcServerConnection terminatedConnection in terminatedConnections)
                        {
                            DebugLog("Disposing server connection: " + terminatedConnection.GetServerKey());
                            terminatedConnection.Dispose();
                        }
                    }).Start();
                }
            }
        }

        public int NumActiveConnections()
        {
            int activeConnections = 0;
            foreach (IpcServerConnection connection in connections)
            {
                if (connection.connectionStatus == IpcUtils.ConnectionStatus.Connected)
                {
                    activeConnections += 1;
                }
            }

            return activeConnections;
        }

        public void Dispose()
        {
            stopToken.Cancel();
            foreach (IpcServerConnection connection in connections)
            {
                connection.Dispose();
            }
            connectEvent.Dispose();
            disconnectEvent.Dispose();
            stopToken.Dispose();
        }
    }
}
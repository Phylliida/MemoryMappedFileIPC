using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        ConcurrentQueueWithNotification<IpcServerConnection> connectionsToDispose = new ConcurrentQueueWithNotification<IpcServerConnection>();

        public event IpcServerConnection.RecievedBytesCallback RecievedBytes;
        Thread disposingThread;
        Thread updateThread;
        
        public ManualResetEvent connectEvent = new ManualResetEvent(false);
        public ManualResetEvent disconnectEvent = new ManualResetEvent(true);
        object connectEventLock = new object();
        object disposeLock = new object();

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
            updateThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        UpdateConnectionEvents();
                        CheckListeners();
                        Task.Delay(millisBetweenPing, stopToken.Token).GetAwaiter().GetResult();
                    }
                }
                catch (TaskCanceledException)
                {

                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception e)
                {
                    DebugLog("Exception in update thread:" + e.GetType() + " " + e.Message + " " + e.StackTrace);
                }
            });

            disposingThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        // we need to do this in this order to ensure no race conditions with dispose logic at end
                        if (connectionsToDispose.TryPeek(out IpcServerConnection _, -1, stopToken.Token))
                        {
                            lock (disposeLock)
                            {
                                if (stopToken != null && !stopToken.IsCancellationRequested)
                                {
                                    if (connectionsToDispose.TryDequeue(out IpcServerConnection connectionToDispose, 0, stopToken.Token))
                                    {
                                        connectionToDispose.Dispose();
                                    }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {

                }
            });

            disposingThread.Start();
            updateThread.Start();
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
            // once it connects, we need to open a new channel for new people that want to connect
            connection.OnRecievedBytes += (byte[][] bytes) =>
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
                if (stopToken == null || stopToken.IsCancellationRequested)
                {
                    return;
                }
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
            lock(connectEventLock)
            {
                if (stopToken == null || stopToken.IsCancellationRequested)
                {
                    return;
                }
                // if no one is waiting for connection, make a new one (maybe an error was thrown in connection attempt)
                bool anyAreWaiting = connections.Any(
                        (c) =>
                            c.connectionStatus == IpcUtils.ConnectionStatus.WaitingForConnection);
                if (!anyAreWaiting)
                {
                    AddNewListener();
                }

                // remove all terminated connections from bag and dispose them
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
                    foreach (IpcServerConnection terminatedConnection in terminatedConnections)
                    {
                        connectionsToDispose.Enqueue(terminatedConnection);
                    }
                }
            }
        }

        public int NumActiveConnections()
        {
            // todo: could this throw exception if modified while ToList is happening?
            return connections.ToList() // ToList incase it's modified while iterating
                .Where(c => c.connectionStatus == ConnectionStatus.Connected)
                .Count();
        }

        public void Dispose()
        {
            lock(disposeLock)
            {
                lock (connectEventLock)
                {
                    if (stopToken != null)
                    {
                        stopToken.Cancel();
                    }
                    foreach (IpcServerConnection connection in connections)
                    {
                        connection.Dispose();
                    }
                    while (connectionsToDispose.queue.TryDequeue(out IpcServerConnection connectionToDispose)) {
                        connectionToDispose.Dispose();
                    }
                    // easiest way to clear it bc it doesn't have .Clear or any way to remove (??)
                    connections = new ConcurrentBag<IpcServerConnection>();

                    if (connectEvent != null)
                    {
                        connectEvent.Dispose();
                        connectEvent = null;
                    }
                    if (disconnectEvent != null)
                    {
                        disconnectEvent.Dispose();
                        disconnectEvent = null;
                    }
                    if (stopToken != null)
                    {
                        stopToken.Dispose();
                        stopToken = null;
                    }
                }
            }
            // needs to be done outside of disposingLock since it requests the lock
            if (disposingThread != null)
            {
                disposingThread.Join();
                disposingThread = null;
            }
            if (updateThread != null)
            {
                updateThread.Join();
                updateThread = null;
            }
            // do this after disposing of disposingThread because it uses it
            if (connectionsToDispose != null)
            {
                connectionsToDispose.Dispose();
                connectionsToDispose = null;
            }
        }
    }
}
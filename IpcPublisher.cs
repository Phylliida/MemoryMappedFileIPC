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
    public class IpcPublisher : IDisposable
    {
        CancellationTokenSource stopToken = new CancellationTokenSource();
        ConcurrentDictionary<int, IpcClientConnection> connections = new ConcurrentDictionary<int, IpcClientConnection>();
        ConcurrentQueueWithNotification<IpcClientConnection> connectionsToDispose = new ConcurrentQueueWithNotification<IpcClientConnection>();
        int processId;
        public int millisBetweenPing;
        public string channelName;
        public string serverDirectory;

        Thread searchThread;
        Thread disposingThread;

        public ManualResetEvent connectEvent = new ManualResetEvent(false);
        public ManualResetEvent disconnectEvent = new ManualResetEvent(true);
        object connectEventLock = new object();
        object disposeLock = new object();

        DebugLogType DebugLog;

        public IpcPublisher(string channelName, string serverDirectory, int millisBetweenPing = 1000, DebugLogType logger=null)
        {
            this.serverDirectory = serverDirectory;
            Directory.CreateDirectory(serverDirectory);
            this.DebugLog = logger;
            if (DebugLog == null)
            {
                this.DebugLog = (x) => { };
            }
            this.channelName = channelName;
            this.processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            this.millisBetweenPing = millisBetweenPing;

            // occasionally check to see if new publishers are available
            // todo: use file listener api to look for changes in the server files directly
            // but for the number of servers we have, this is fine enough
            searchThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        ConnectToAvailableServers();
                        UpdateConnectionEvents();
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
                    DebugLog("Exception in search thread:" + e.GetType() + " " + e.Message + " " + e.StackTrace);
                }
            });
            searchThread.Start();

            disposingThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        // we need to do this in this order to ensure no race conditions with dispose logic at end
                        if (connectionsToDispose.TryPeek(out IpcClientConnection _, -1, stopToken.Token))
                        {
                            lock (disposeLock)
                            {
                                if (stopToken != null && !stopToken.IsCancellationRequested)
                                {
                                    if (connectionsToDispose.TryDequeue(out IpcClientConnection connectionToDispose, 0, stopToken.Token))
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
                catch (Exception e)
                {
                    DebugLog("Exception in disposing thread:" + e.GetType() + " " + e.Message + " " + e.StackTrace);
                }
            });

            disposingThread.Start();
        }

        public void Publish(byte[] bytes)
        {
            Publish(new byte[][] { bytes });
        }

        public void Publish(byte[][] bytes)
        {
            lock (connectEventLock)
            {
                if (stopToken == null || stopToken.IsCancellationRequested)
                {
                    return;
                }
                foreach (KeyValuePair<int, IpcClientConnection> connection in connections)
                {
                    if (connection.Value.connectionStatus == IpcUtils.ConnectionStatus.Connected)
                    {
                        connection.Value.SendBytes(bytes);
                    }
                }
            }
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


        
        void ConnectToAvailableServers() {
            lock(connectEventLock)
            {
                if (stopToken == null || stopToken.IsCancellationRequested)
                {
                    return;
                }
                // remove all terminated connections, this ensures we can try to reconnect again to a process that disconnected previously
                foreach (KeyValuePair<int, IpcClientConnection> terminatedConnection in
                    connections.Where(c => c.Value.connectionStatus == ConnectionStatus.Terminated).ToList())
                {
                    DebugLog("Removing terminated connection to process " + terminatedConnection.Key + " from connections");
                    connections.TryRemove(terminatedConnection.Key, out IpcClientConnection removingConnection);
                    // dispose the terminated connection
                    connectionsToDispose.Enqueue(removingConnection);
                }
                
                foreach (IpcServerInfo server in IpcUtils.GetLoadedServers(serverDirectory, this.millisBetweenPing * 2, stopToken.Token))
                {
                    if (server.baseKey == channelName &&
                        server.connectionStatus == IpcUtils.ConnectionStatus.WaitingForConnection &&
                        server.processId != processId &&
                        !connections.ContainsKey(server.processId))
                    {
                        IpcClientConnection clientConnection = new IpcClientConnection(
                            IpcUtils.GetServerKey(server.baseKey, Guid.Parse(server.guid)),
                            this.millisBetweenPing,
                            2,
                            stopToken,
                            DebugLog);
                        connections[server.processId] = clientConnection;
                        // on disconnect, try to reconnect
                        //clientConnection.OnDisconnect += () =>
                        //{
                            // important we do this after reset connect event to avoid
                            // race condition where we connect between IsConnected test and reset event
                            //if (stopToken != null && !stopToken.IsCancellationRequested)
                            //{
                            // this has a race condition where
                            //  1. The test above passes through
                            //  2. The dispose grabs lock
                            //  3. Dispose tries to dispose, which calls dispose on this connection
                            //  and waits for the thread that calls OnDisconnect to finish
                            //  4. It's stuck because that lock is already claimed
                            // instead we'll just intermittendly update connection events
                            // with thread above
                            //    UpdateConnectionEvents();
                                // IT IS IMPORT TO NOT PUT ConnectToAvailableServers() HERE
                                // OTHERWISE YOU CAN GET A LOOP IF SERVER TERMINATED WHERE IT RECURSES OVER AND OVER
                            //}
                        //};
                        //clientConnection.OnConnect += () => {
                        //    if (stopToken != null && !stopToken.IsCancellationRequested)
                        //    {
                        //        UpdateConnectionEvents();
                        //    }
                        //};
                        clientConnection.Init();
                    }
                }
            }
        }

        public int NumActiveConnections()
        {
            return connections.ToList() // ToList incase it's modified while iterating
           .Where(c => c.Value.connectionStatus == ConnectionStatus.Connected)
           .Count();
        }

        public void Dispose()
        {
            lock(disposeLock)
            {
                lock(connectEventLock)
                {
                    if (stopToken != null)
                    {
                        stopToken.Cancel();
                    }
                    foreach (KeyValuePair<int, IpcClientConnection> connection in connections.ToList())
                    {
                        connection.Value.Dispose();
                    }
                    connections.Clear();

                    while (connectionsToDispose.queue.TryDequeue(out IpcClientConnection connectionToDispose))
                    {
                        connectionToDispose.Dispose();
                    }

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
            // let searchThread finish, but outside connectEvent lock since it requests it
            if (searchThread != null)
            {
                searchThread.Join();
                searchThread = null;
            }
            // same for disposing thread, needs to join outside the locks
            if (disposingThread != null)
            {
                disposingThread.Join();
                disposingThread = null;
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
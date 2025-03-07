using System;
using System.Threading;

namespace MemoryMappedFileIPC
{
   public class IpcClientConnection : IDisposable {

        ConcurrentQueueWithNotification<byte[][]> bytesToSend = new ConcurrentQueueWithNotification<byte[][]>();
        Thread dataThread;
        Thread pingThread;
        readonly CancellationTokenSource parentStopTokenSource;
        CancellationTokenSource selfStopTokenSource;
        CancellationTokenSource stopToken;

        public IpcUtils.ConnectionStatus connectionStatus;
        public void SendBytes(byte[][] bytes)
        {
            bytesToSend?.Enqueue(bytes);
        }

        public void SendBytes(byte[] bytes)
        {
            SendBytes(new byte[][] { bytes });
        }



        private readonly string idOfServer;
        private readonly int millisBetweenPing;
        private readonly int timeoutMultiplier;

        readonly IpcUtils.DebugLogType DebugLog;
        public IpcClientConnection(string idOfServer, int millisBetweenPing, int timeoutMultiplier,
            CancellationTokenSource stopToken, IpcUtils.DebugLogType DebugLog=null)
        {
            this.DebugLog = DebugLog;
            if (DebugLog == null)
            {
                this.DebugLog = (x) => { };
            }
            this.parentStopTokenSource = stopToken;
            this.selfStopTokenSource = new CancellationTokenSource();
            this.stopToken = CancellationTokenSource.CreateLinkedTokenSource(parentStopTokenSource.Token, selfStopTokenSource.Token);
            this.idOfServer = idOfServer;
            this.millisBetweenPing = millisBetweenPing;
            this.timeoutMultiplier = timeoutMultiplier;
            this.connectionStatus = IpcUtils.ConnectionStatus.WaitingForConnection;
        }


        public void Init() {
             // each connection is actually two connections
             // one that is just for sending keepalive pings
             // the other for the actual data
             
            // we need to send keepalive pings because named pipes don't have easy
            // ways of telling when connection died in certain cases
            // however if we required a keepalive ping every x seconds, that would prevent us from
            // sending large chunks of data that took more than x seconds to recieve
            // (because a timeout would be triggered, which is indistinguishable from connection dropped)

            // this "two connection" method allows us to be regularly sending keepalive pings
            // while simultaneously sending large chunks of data


             // server thread
            dataThread = new Thread(() =>
            {
                string id = idOfServer + "data";
                try
                {
                    DebugLog("Connecting to " + id);
                    // "." means local computer which is what we want
                    // PipeOptions.Asynchronous is very important!! Or ConnectAsync won't stop when stopToken is canceled
                    using (MemoryMappedFileConnection dataClient = new MemoryMappedFileConnection(id, IpcUtils.BUFFER_SIZE, isWriter: true))
                    {
                        dataClient.Connect(stopToken.Token, millisBetweenPing * timeoutMultiplier);
                        this.connectionStatus = IpcUtils.ConnectionStatus.Connected;
                        while (!stopToken.IsCancellationRequested)
                        {
                            // send messages
                            while (bytesToSend.TryDequeue(out byte[][] bytes, -1, stopToken.Token))
                            {
                                WriteBytes(dataClient, bytes, stopToken.Token);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugLog("Got error in client data connection, disconnecting:" + e.Message + e.StackTrace);
                }
                finally
                {
                    DebugLog("Terminating client data connection to " + id);
                    selfStopTokenSource.Cancel();
                }
            });

            pingThread = new Thread(() =>
            {
                string id = idOfServer + "ping";
                try
                {
                    DebugLog("Connecting to " + id);
                    // "." means local computer which is what we want
                    // PipeOptions.Asynchronous is very important!! Or ConnectAsync won't stop when stopToken is canceled
                    using (SharedEventWaitHandle pingClientHandle = new SharedEventWaitHandle(id + "clientHandle", false, true))
                    {
                        using (SharedEventWaitHandle pingServerHandle = new SharedEventWaitHandle(id + "serverHandle", false, true))
                        {
                            while (!stopToken.IsCancellationRequested)
                            {
                                DebugLog(id + " client Setting client handle");
                                pingClientHandle.waitHandle.Set(); // let them know we are here
                                DebugLog(id + " client Waiting for server handle for " + (millisBetweenPing * timeoutMultiplier) + " millis");
                                pingServerHandle.WaitOrCancel(stopToken.Token, millisBetweenPing * timeoutMultiplier); // wait for response
                                DebugLog(id + " client Got ping, doing sleep for " + millisBetweenPing);
                                Thread.Sleep(millisBetweenPing); // it would be nice to do cancellable sleep but that risks taking longer if async gets full
                                DebugLog(id + " client Done doing sleep for " + millisBetweenPing);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugLog("Got error in client ping connection, disconnecting:" + e.GetType() + " " + e.Message + e.StackTrace);
                }
                finally
                {
                    DebugLog("Terminating client ping connection to " + id);
                    this.connectionStatus = IpcUtils.ConnectionStatus.Terminated;
                    selfStopTokenSource.Cancel();
                }
            });

            dataThread.Start();
            pingThread.Start();
        }

        public void Dispose()
        {
            DebugLog("Started disposing client " + idOfServer);
            selfStopTokenSource?.Cancel();
            if (dataThread != null)
            {
                dataThread.Join();
                dataThread = null;
            }
            if (pingThread != null)
            {
                pingThread.Join();
                pingThread = null;
            }
            if (bytesToSend != null)
            {
                bytesToSend.Dispose();
                bytesToSend = null;
            }
            if (stopToken != null)
            {
                stopToken.Dispose();
                stopToken = null;
            }
            if (selfStopTokenSource != null)
            {
                selfStopTokenSource.Dispose();
                selfStopTokenSource = null;
            }
            DebugLog("Finished disposing client " + idOfServer);
        }

        public void WriteBytes(MemoryMappedFileConnection connection, byte[][] bytes, CancellationToken stopToken, int millisTimeout=-1) {
            byte[] lenBytes = BitConverter.GetBytes(bytes.Length);
            connection.WriteData(lenBytes, 0, 4, stopToken, millisTimeout);
            for (int i = 0; i < bytes.Length; i++)
            {
                connection.WriteData(bytes[i], 0, bytes[i].Length, stopToken, millisTimeout);
            }
        }

    }
}
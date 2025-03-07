using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryMappedFileIPC
{
    public class IpcUtils
    {
        // gross stuff to let us async wait on wait handles
        /// <summary>
        /// Returns WaitHandle.WaitTimeout if timeout occurs
        /// Otherwise, index of first wait handle waited on
        /// If timeout < 0, there is no timeout
        /// </summary>
        /// <param name="handles"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public static async Task<int> WaitAny(WaitHandle[] handles, int timeout=-1)
        {
            using (CancellationTokenSource doneToken = new CancellationTokenSource())
            {
                List<Task> tasks = handles.Select(handle => WaitOneAsync(handle, doneToken.Token)).ToList();
                if (timeout >= 0)
                {
                    tasks.Add(Task.Delay(timeout));
                }
                int resultI = tasks.IndexOf(await Task.WhenAny(tasks));
                // clean up all waiting tasks
                doneToken.Cancel();
                if (resultI == tasks.Count - 1 && timeout >= 0)
                {
                    // value we should return if timeout
                    resultI = WaitHandle.WaitTimeout;
                }
                return resultI;
            }
        }

        public static Task WaitOneAsync(WaitHandle waitHandle, CancellationToken cancellationToken)
        {
            if (waitHandle == null)
                throw new ArgumentNullException("waitHandle");

            var tcs = new TaskCompletionSource<bool>();
            var rwh = ThreadPool.RegisterWaitForSingleObject(waitHandle,
                delegate { Interlocked.Exchange(ref tcs, null)?.TrySetResult(true); }, null, -1, true);
            var t = tcs.Task;
            // this unregisters in either case but not both because once one does it it's now null
            t.ContinueWith((antecedent) => {
                Interlocked.Exchange(ref tcs, null);
                Interlocked.Exchange(ref rwh, null)?.Unregister(null);
            });
            // todo: does this actually fully clean it up? or do we need to domore?
            cancellationToken.Register(() => {
                Interlocked.Exchange(ref tcs, null)?.TrySetResult(true);
            });
            return t;
        }



        public static byte PING_MESSAGE = 1;
        public static byte DATA_MESSAGE = 2;

        // important none are zero because that's the default bytes recieved when connection dies
        public enum ResponseType {
            Ping=1,
            Data=2,
            Error=3
        }

       public enum ConnectionStatus {
            WaitingForConnection,
            Connected,
            Terminated
        }
        public class CanceledException : Exception {
        
        }

        public class DisconnectedException : Exception
        {

        }


        public static string GetServerKey(string baseKey, Guid guid)
        {
            return baseKey + guid.ToString();
        }

        public const int BUFFER_SIZE = 2048 * 16;
        public const int PING_BUFFER_SIZE = 128;

        
        public static long TimeMillis() {
            return DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        public static string GuidToConnectionPath(Guid guid, string serverDirectory) {
            return Path.Combine(serverDirectory, guid.ToString() + ".json");
        }

        /*
        public static string GetServerDirectory(string serverDirectory) {
            string serverDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TefssaCoil", "ResoniteDataWrapper", "Servers");
            // make if not exists
            Directory.CreateDirectory(serverDirectory);
            return serverDirectory;
        }
        */

        public static int WAIT_MILLIS = 100;
        public static int NUM_RETRIES = 6;


        public static void SafeDeleteFile(string path, CancellationToken stopToken)
        {
            for (int i = 0; i < NUM_RETRIES && File.Exists(path); i++)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    if (i == NUM_RETRIES - 1)
                    {
                        // we failed
                        return;
                    }
                    else
                    {
                        try
                        {
                            Task.Delay(WAIT_MILLIS, stopToken).GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        public static string SafeReadAllText(string path, CancellationToken stopToken)
        {
            for (int i = 0; i < NUM_RETRIES; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var Strings = new StreamReader(fs))
                    {
                        return Strings.ReadToEnd();
                    }
                }
                catch (Exception)
                {
                    if (i == NUM_RETRIES - 1)
                    {
                        // we failed
                        return null;
                    }
                    else
                    {
                        try
                        {
                            Task.Delay(WAIT_MILLIS, stopToken).GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            return null;
                        }
                        catch (OperationCanceledException)
                        {
                            return null;
                        }
                    }
                }
            }
            // fail to read
            return null;
        }

        public static byte[] SafeReadAllBytes(string path, CancellationToken stopToken)
        {
            for (int i = 0; i < NUM_RETRIES; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var Strings = new BinaryReader(fs))
                    {
                        return Strings.ReadBytes((int)fs.Length);
                    }
                }
                catch (Exception)
                {
                    if (i == NUM_RETRIES - 1)
                    {
                        // we failed
                        return null;
                    }
                    else
                    {
                        try
                        {
                            Task.Delay(WAIT_MILLIS, stopToken).GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            return null;
                        }
                        catch (OperationCanceledException)
                        {
                            return null;
                        }
                    }
                }
            }
            // fail to read
            return null;
        }


        public delegate void DebugLogType(string msg);

        public static void SafeWriteAllText(string path, string contents, CancellationToken stopToken)
        {
            // we need this retry logic because other stuff might be trying to access it
            for (int i = 0; i < NUM_RETRIES; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.Write(contents);
                        return;
                    }
                }
                catch (Exception)
                {
                    if (i == NUM_RETRIES - 1)
                    {
                        // we failed
                        return;
                    }
                    else
                    {
                        try
                        {
                            Task.Delay(WAIT_MILLIS, stopToken).GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
         }


        public static void SafeWriteAllBytes(string path, byte[] contents, CancellationToken stopToken)
        {
            // we need this retry logic because other stuff might be trying to access it
            for (int i = 0; i < NUM_RETRIES; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sw = new BinaryWriter(fs))
                    {
                        sw.Write(contents);
                        return;
                    }
                }
                catch (Exception)
                {
                    if (i == NUM_RETRIES - 1)
                    {
                        // we failed
                        return;
                    }
                    else
                    {
                        try
                        {
                            Task.Delay(WAIT_MILLIS, stopToken).GetAwaiter().GetResult();
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fetches loaded servers from the config files
        /// </summary>
        /// <param name="keepaliveMillis">How long since last update we allow before a server is ignored</param>
        public static IEnumerable<IpcServerInfo> GetLoadedServers(string serverDirectory, long keepAliveMillis, CancellationToken stopToken) {
            List<IpcServerInfo> servers = new List<IpcServerInfo>();
            foreach (string server in Directory.GetFiles(serverDirectory, "*.json")) {
                try {
                    byte[] text = SafeReadAllBytes(server, stopToken);
                    if (text != null)
                    {
                        IpcServerInfo info = SerializationUtils.DecodeObject<IpcServerInfo>(text);
                        long timeSinceUpdated = TimeMillis() - info.timeOfLastUpdate;
                        if (timeSinceUpdated < keepAliveMillis)
                        {
                            servers.Add(info);
                        }
                        // cleanup very old ones so directory not cluttered
                        else if (timeSinceUpdated > keepAliveMillis * 4)
                        {
                            SafeDeleteFile(server, stopToken);
                        }
                    }
                    // we are canceled, bail
                    else if (stopToken.IsCancellationRequested)
                    {
                        servers.Clear();
                        break;
                    }
                }
                // sometimes they are just left as empty, clean those up
                catch (Exception) {
                    long lastWriteMillis = DateTimeToMillis(System.IO.File.GetLastWriteTimeUtc(server));
                    long curMillis = DateTimeToMillis(DateTime.UtcNow);
                    if (curMillis - lastWriteMillis > keepAliveMillis)
                    {
                        SafeDeleteFile(server, stopToken);
                    }
                }
            }

            return servers;
        }

        public static long DateTimeToMillis(DateTime datetime)
        {
            return (long)datetime.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
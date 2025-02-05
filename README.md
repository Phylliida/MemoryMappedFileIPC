# MemoryMappedFileIPC
Pure c# IPC library using a memory mapped file (shared memory).

Pub/sub interface, compatible with most .NET versions. Only works on Windows.

## What is it?

Memory mapped files, also known as shared memory, allow for efficient communication between processes. They are much faster than websockets, named pipes, and TCP or UDP.


However, the C# interface to memory mapped files is low-level. In particular, implementing pub-sub for multiple publishers and subscribers is non-trivial.

This is a higher-level, thread-safe wrapper that lets you do robust IPC easily.

## How to use?

### Setup
First, pick a directory that'll hold the list of existing connections. Each alive connection will be a json file in this directory.

A simple option is something like

```c#
string serverDirectory = 
	Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
		"YourApplication",
		"IPCConnections",
		"Servers"
	);
```

Next, select a channel name. Only publishers and subscribers that share this label will be able to communicate.

```c#
string channelName = "testChannel";
```

### Sending messages

Create an `IpcPublisher`:

```c#
IpcPublisher publisher = new IpcPublisher(channelName, serverDirectory);
```

You can then call

```c#
byte[] byteArray = ...; // make your bytes however you want
publisher.Publish(byteArray);
```

to send some bytes.

Because there could be multiple publishers and subscribers, sending multiple byte arrays in sequence might be non-trivial.

To address this (and to avoid needing to copy to one big byte array), it is also supported to do

```c#
byte[][] arrayOfByteArrays = ...; // make your bytes however you want
publisher.Publish(arrayOfByteArrays);
```

Once you are done with publisher, make sure to call

```c#
publisher.Dispose()
```

If you want to know when our publisher has connected to any subscriber, you can use

```c#
publisher.connectEvent.WaitOne()
```

and for disconnections from all subscribers, you can use

```c#
publisher.disconnectEvent.WaitOne()
```

Alternatively, just look for when

```c#
publisher.NumActiveConnections()
```

is greater than zero.


### Recieving messages

Create an `IpcSubscriber`:

```c#
IpcSubscriber subscriber = new IpcSubscriber(channelName, serverDirectory);
```

Now you can subscribe to the message event:

```c#
subscriber.RecievedBytes += (byte[][] bytes) =>
{
	// process your bytes
	// if you just sent one byte array above instead of a list, it'll be bytes[0]
}
```

Make sure to call

```c#
subscriber.Dispose()
```

once you are done using it.

there is a `connectEvent`, `disconnectEvent`, and `NumActiveConnections()` available, just like with the publisher.

### Bidirectional communication?

Just make each side have both a publisher and a subscriber. Publishers will only connect to subscribers with a different process id.

## Implementation Details

Every pair of subscriber-publisher has it's own memory mapped file. This simplifies things and allows each connection to transfer data at it's own pace.

### Connections

Subscribers will always have one open connection (not connected yet), to ensure that there's always room for another publisher to connect to them.

One subscriber recieving messages from multiple publishers is supported.

One publisher sending to multiple subscribers is supported.

All subscribers will recieve all messages sent by all publishers with the same channelName.

Every publisher will create it's own .json file in the directory provided. This allows the subscribers to just scan this directory occasionally to find new connections.

### Ping

Every sub/pub pair actually has two connections:
- a "data" connection which is used to send data
- a "ping"/keepalive connection which is used to check if the connection is still alive

the ping connection will send a ping every 1 second, you can adjust this by passing in the millisBetweenPing parameter.

But make sure subscriber and publisher have the same value for millisBetweenPing or they will disconnect.

These are seperate connections to allow for the data thread to take as long as you want processing the data.

### Syncronization 

I use `\\Global` shared event wait handles (see MemoryMappedFileServer.cs).

This is a Windows-only feature that lets me have cross process shared wait handles to let me know when reading and writing have completed.

This is the only part of this library that is Windows-specific, if someone can figure out a non-windows shared event wait handle implemention I would be happy to accept a PR.

### Dispose/Async

This library is fully thread-safe.

Also, I use only async operations which all recieve a Cancellation token. This ensures that calling Dispose() will immediately terminate all threads and dispose of all resources properly.

## Alternatives

[https://github.com/cloudtoid/interprocess](https://github.com/cloudtoid/interprocess) seems good, however named semaphores weren't supported on the version of .net I wanted to use, which is why this library exists.

[https://github.com/justinstenning/SharedMemory/](https://github.com/justinstenning/SharedMemory/) is a great alternative, however it doesn't support any number of publishers and subscribers on the same channel.

Named pipes are an alternative to shared memory. However they are slower, and the async read/write methods aren't supported in the version of mono I was using (Resonite).
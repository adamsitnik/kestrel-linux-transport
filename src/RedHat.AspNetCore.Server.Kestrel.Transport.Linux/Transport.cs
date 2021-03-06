using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    internal class Transport : IConnectionListener
    {
        private enum State
        {
            Created,
            Binding,
            Bound,
            Unbinding,
            Unbound,
            Stopping,
            Stopped
        }
        // Kestrel LibuvConstants.ListenBacklog
        private const int ListenBacklog = 128;

        private readonly LinuxTransportOptions _transportOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private State _state;
        private readonly object _gate = new object();
        private ITransportActionHandler[] _threads;
        private IAsyncEnumerator<ConnectionContext> _acceptEnumerator;

        public EndPoint EndPoint { get; }

        public Transport(EndPoint endPoint, LinuxTransportOptions transportOptions, ILoggerFactory loggerFactory)
        {
            if (transportOptions == null)
            {
                throw new ArgumentException(nameof(transportOptions));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentException(nameof(loggerFactory));
            }
            if (endPoint == null)
            {
                throw new ArgumentException(nameof(endPoint));
            }

            EndPoint = endPoint;
            _transportOptions = transportOptions;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Transport>();
            _threads = Array.Empty<TransportThread>();
        }

        public async Task BindAsync()
        {
            AcceptThread acceptThread;
            TransportThread[] transportThreads;

            lock (_gate)
            {
                if (_state != State.Created)
                {
                    ThrowInvalidOperation();
                }
                _state = State.Binding;

                switch (EndPoint)
                {
                    case IPEndPoint ipEndPoint:
                        acceptThread = null;
                        transportThreads = CreateTransportThreads(ipEndPoint, acceptThread: null);
                        break;
                    case UnixDomainSocketEndPoint unixDomainSocketEndPoint:
                        var socketPath = unixDomainSocketEndPoint.ToString();
                        var unixDomainSocket = Socket.Create(AF_UNIX, SOCK_STREAM, 0, blocking: false);
                        File.Delete(socketPath);
                        unixDomainSocket.Bind(socketPath);
                        unixDomainSocket.Listen(ListenBacklog);
                        acceptThread = new AcceptThread(unixDomainSocket);
                        transportThreads = CreateTransportThreads(ipEndPoint: null, acceptThread);
                        break;
                    case FileHandleEndPoint fileHandleEndPoint:
                        var fileHandleSocket = new Socket((int)fileHandleEndPoint.FileHandle);
                        acceptThread = new AcceptThread(fileHandleSocket);
                        transportThreads = CreateTransportThreads(ipEndPoint: null, acceptThread);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown ListenType: {EndPoint.GetType()}.");
                }

                _threads = new ITransportActionHandler[transportThreads.Length + (acceptThread != null ? 1 : 0)];
                _threads[0] = acceptThread;
                for (int i = 0; i < transportThreads.Length; i++)
                {
                    _threads[i + (acceptThread == null ? 0 : 1)] = transportThreads[i];
                }

                _logger.LogDebug($@"BindAsync {EndPoint}: TC:{_transportOptions.ThreadCount} TA:{_transportOptions.SetThreadAffinity} IC:{_transportOptions.ReceiveOnIncomingCpu} DA:{_transportOptions.DeferAccept}");
            }

            var tasks = new Task[transportThreads.Length];
            for (int i = 0; i < transportThreads.Length; i++)
            {
                tasks[i] = transportThreads[i].BindAsync();
            }
            try
            {
                await Task.WhenAll(tasks);

                if (acceptThread != null)
                {
                    await acceptThread.BindAsync();
                }

                _acceptEnumerator = AcceptConnections();

                lock (_gate)
                {
                    if (_state == State.Binding)
                    {
                        _state = State.Bound;
                    }
                    else
                    {
                        ThrowInvalidOperation();
                    }
                }
            }
            catch
            {
                await DisposeAsync();
                throw;
            }
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.CanBeCanceled)
            {
                throw new NotImplementedException("AcceptAsync does not currently support cancellation via a token.");
            }

            lock (_gate)
            {
                if (_state >= State.Stopping)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }
            }

            if (await _acceptEnumerator.MoveNextAsync())
            {
                return _acceptEnumerator.Current;
            }

            // null means we're done...
            return null;
        }

        private static int s_threadId = 0;

        private TransportThread[] CreateTransportThreads(IPEndPoint ipEndPoint, AcceptThread acceptThread)
        {
            var threads = new TransportThread[_transportOptions.ThreadCount];
            IList<int> preferredCpuIds = null;
            if (_transportOptions.SetThreadAffinity)
            {
                preferredCpuIds = GetPreferredCpuIds();
            }
            int cpuIdx = 0;
            for (int i = 0; i < _transportOptions.ThreadCount; i++)
            {
                int cpuId = preferredCpuIds == null ? -1 : preferredCpuIds[cpuIdx++ % preferredCpuIds.Count];
                int threadId = Interlocked.Increment(ref s_threadId);
                var thread = new TransportThread(ipEndPoint, _transportOptions, acceptThread, threadId, cpuId, _loggerFactory);
                threads[i] = thread;
            }
            return threads;
        }

        private IList<int> GetPreferredCpuIds()
        {
            if (!_transportOptions.CpuSet.IsEmpty)
            {
                return _transportOptions.CpuSet.Cpus;
            }
            var ids = new List<int>();
            bool found = true;
            int level = 0;
            do
            {
                found = false;
                foreach (var socket in CpuInfo.GetSockets())
                {
                    var cores = CpuInfo.GetCores(socket);
                    foreach (var core in cores)
                    {
                        var cpuIdIterator = CpuInfo.GetCpuIds(socket, core).GetEnumerator();
                        int d = 0;
                        while (cpuIdIterator.MoveNext())
                        {
                            if (d++ == level)
                            {
                                ids.Add(cpuIdIterator.Current);
                                found = true;
                                break;
                            }
                        }
                    }
                }
                level++;
            } while (found && ids.Count < _transportOptions.ThreadCount);
            return ids;
        }

        public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_state <= State.Unbinding)
                {
                    _state = State.Unbinding;
                }
                else
                {
                    return;
                }
            }
            var tasks = new Task[_threads.Length];
            for (int i = 0; i < _threads.Length; i++)
            {
                tasks[i] = _threads[i].UnbindAsync();
            }
            await Task.WhenAll(tasks);
            lock (_gate)
            {
                if (_state == State.Unbinding)
                {
                    _state = State.Unbound;
                }
                else
                {
                    ThrowInvalidOperation();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_state <= State.Stopping)
                {
                    _state = State.Stopping;
                }
                else
                {
                    return;
                }
            }
            var tasks = new Task[_threads.Length];
            for (int i = 0; i < _threads.Length; i++)
            {
                tasks[i] = _threads[i].StopAsync();
            }
            await Task.WhenAll(tasks);
            lock (_gate)
            {
                if (_state == State.Stopping)
                {
                    _state = State.Stopped;
                }
                else
                {
                    ThrowInvalidOperation();
                }
            }
        }

        private async IAsyncEnumerator<ConnectionContext> AcceptConnections()
        {
            var slots = new Task<(ConnectionContext, int)>[_threads.Length];
            // This is the task we'll put in the slot when each listening completes. It'll prevent
            // us from having to shrink the array. We'll just loop while there are active slots.
            var incompleteTask = new TaskCompletionSource<(ConnectionContext, int)>().Task;

            var remainingSlots = slots.Length;

            // Issue parallel accepts on all listeners
            for (int i = 0; i < remainingSlots; i++)
            {
                slots[i] = AcceptAsync(_threads[i], i);
            }

            while (remainingSlots > 0)
            {
                // Calling GetAwaiter().GetResult() is safe because we know the task is completed
                (var connection, var slot) = (await Task.WhenAny(slots)).GetAwaiter().GetResult();

                // If the connection is null then the listener was closed
                if (connection == null)
                {
                    remainingSlots--;
                    slots[slot] = incompleteTask;
                }
                else
                {
                    // Fill that slot with another accept and yield the connection
                    slots[slot] = AcceptAsync(_threads[slot], slot);
                    yield return connection;
                }
            }

            static async Task<(ConnectionContext, int)> AcceptAsync(ITransportActionHandler transportThread, int slot)
            {
                return (await transportThread.AcceptAsync(), slot);
            }
        }

        private void ThrowInvalidOperation()
        {
            throw new InvalidOperationException($"Invalid operation: {_state}");
        }
    }
}
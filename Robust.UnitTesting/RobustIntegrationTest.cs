using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Robust.Client;
using Robust.Client.Interfaces;
using Robust.Server;
using Robust.Server.Interfaces;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using FrameEventArgs = Robust.Shared.Timing.FrameEventArgs;
using ServerEntryPoint = Robust.Server.EntryPoint;

namespace Robust.UnitTesting
{
    /// <summary>
    ///     Base class allowing you to implement integration tests.
    /// </summary>
    /// <remarks>
    ///     Integration tests allow you to act upon a running server as a whole,
    ///     contrary to unit testing which tests, well, units.
    /// </remarks>
    public abstract partial class RobustIntegrationTest
    {
        /// <summary>
        ///     Start an instance of the server and return an object that can be used to control it.
        /// </summary>
        protected virtual ServerIntegrationInstance StartServer(ServerIntegrationOptions options = null)
        {
            return new ServerIntegrationInstance(options);
        }

        /// <summary>
        ///     Start a headless instance of the client and return an object that can be used to control it.
        /// </summary>
        protected virtual ClientIntegrationInstance StartClient(ClientIntegrationOptions options = null)
        {
            return new ClientIntegrationInstance(options);
        }

        /// <summary>
        ///     Provides control over a running instance of the client or server.
        /// </summary>
        /// <remarks>
        ///     The instance executes in another thread.
        ///     As such, sending commands to it purely queues them to be ran asynchronously.
        ///     To ensure that the instance is idle, i.e. not executing code and finished all queued commands,
        ///     you can use <see cref="WaitIdleAsync"/>.
        ///     This method must be used before trying to access any state like <see cref="ResolveDependency{T}"/>,
        ///     to prevent race conditions.
        /// </remarks>
        public abstract class IntegrationInstance
        {
            private protected Thread InstanceThread;
            private protected IDependencyCollection DependencyCollection;

            private protected readonly ChannelReader<object> _toInstanceReader;
            private protected readonly ChannelWriter<object> _toInstanceWriter;
            private protected readonly ChannelReader<object> _fromInstanceReader;
            private protected readonly ChannelWriter<object> _fromInstanceWriter;

            private int _currentTicksId = 1;
            private int _ackTicksId;

            private bool _isSurelyIdle;
            private bool _isAlive = true;
            private Exception _unhandledException;

            /// <summary>
            ///     Whether the instance is still alive.
            ///     "Alive" indicates that it is able to receive and process commands.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            public bool IsAlive
            {
                get
                {
                    if (!_isSurelyIdle)
                    {
                        throw new InvalidOperationException(
                            "Cannot read this without ensuring that the instance is idle.");
                    }

                    return _isAlive;
                }
            }

            /// <summary>
            ///     If the server
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            public Exception UnhandledException
            {
                get
                {
                    if (!_isSurelyIdle)
                    {
                        throw new InvalidOperationException(
                            "Cannot read this without ensuring that the instance is idle.");
                    }

                    return _unhandledException;
                }
            }

            private protected IntegrationInstance()
            {
                var toInstance = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                _toInstanceReader = toInstance.Reader;
                _toInstanceWriter = toInstance.Writer;

                var fromInstance = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                _fromInstanceReader = fromInstance.Reader;
                _fromInstanceWriter = fromInstance.Writer;
            }

            /// <summary>
            ///     Resolve a dependency inside the instance.
            ///     This works identical to <see cref="IoCManager.Resolve{T}"/>.
            /// </summary>
            /// <exception cref="InvalidOperationException">
            ///     Thrown if you did not ensure that the instance is idle via <see cref="WaitIdleAsync"/> first.
            /// </exception>
            public T ResolveDependency<T>()
            {
                if (!_isSurelyIdle)
                {
                    throw new InvalidOperationException(
                        "Cannot resolve services without ensuring that the instance is idle.");
                }

                return DependencyCollection.Resolve<T>();
            }

            /// <summary>
            ///     Wait for the instance to go idle, either through finishing all commands or shutting down/crashing.
            /// </summary>
            /// <param name="throwOnUnhandled">
            ///     If true, throw an exception if the server dies on an unhandled exception.
            /// </param>
            /// <param name="cancellationToken"></param>
            /// <exception cref="Exception">
            ///     Thrown if <paramref name="throwOnUnhandled"/> is true and the instance shuts down on an unhandled exception.
            /// </exception>
            public async Task WaitIdleAsync(bool throwOnUnhandled = true, CancellationToken cancellationToken = default)
            {
                try
                {
                    while (_isAlive && _currentTicksId != _ackTicksId)
                    {
                        var msg = await _fromInstanceReader.ReadAsync(cancellationToken);
                        switch (msg)
                        {
                            case ShutDownMessage shutDownMessage:
                            {
                                _isAlive = false;
                                _unhandledException = shutDownMessage.UnhandledException;
                                if (throwOnUnhandled && _unhandledException != null)
                                {
                                    throw new Exception("Waiting instance shut down with unhandled exception",
                                        _unhandledException);
                                }

                                break;
                            }

                            case AckTicksMessage ack:
                            {
                                _ackTicksId = ack.MessageId;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    _isSurelyIdle = true;
                }
            }

            /// <summary>
            ///     Queue for the server to run n ticks.
            /// </summary>
            /// <param name="ticks">The amount of ticks to run.</param>
            public void RunTicks(int ticks)
            {
                _isSurelyIdle = false;
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new RunTicksMessage(ticks, 1 / 60f, _currentTicksId));
            }

            /// <summary>
            ///     Queue for the server to be stopped.
            /// </summary>
            public void Stop()
            {
                _isSurelyIdle = false;
                // Won't get ack'd directly but the shutdown is convincing enough.
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new StopMessage());
            }

            /// <summary>
            ///     Queue for a delegate to be ran inside the main loop of the instance.
            /// </summary>
            public void Post(Action post)
            {
                _isSurelyIdle = false;
                _currentTicksId += 1;
                _toInstanceWriter.TryWrite(new PostMessage(post, _currentTicksId));
            }
        }

        public sealed class ServerIntegrationInstance : IntegrationInstance
        {
            private readonly ServerIntegrationOptions _options;

            internal ServerIntegrationInstance(ServerIntegrationOptions options)
            {
                _options = options;
                InstanceThread = new Thread(_serverMain) {Name = "Server Instance Thread"};
                DependencyCollection = new DependencyCollection();
                InstanceThread.Start();
            }

            private void _serverMain()
            {
                try
                {
                    IoCManager.InitThread(DependencyCollection);
                    ServerIoC.RegisterIoC();
                    IoCManager.Register<INetManager, IntegrationNetManager>(true);
                    IoCManager.Register<IServerNetManager, IntegrationNetManager>(true);
                    IoCManager.Register<IntegrationNetManager, IntegrationNetManager>(true);
                    IoCManager.Register<ICommandLineArgs, CommandLineArgs>(true);
                    _options?.InitIoC?.Invoke();
                    IoCManager.BuildGraph();
                    ServerEntryPoint.SetupLogging();
                    ServerEntryPoint.InitReflectionManager();

                    var server = DependencyCollection.Resolve<IBaseServerInternal>();

                    server.ContentRootDir = "../../";

                    _options?.BeforeStart?.Invoke();
                    if (server.Start())
                    {
                        throw new Exception("Server failed to start.");
                    }

                    var gameLoop = new IntegrationGameLoop(
                        DependencyCollection.Resolve<IGameTiming>(),
                        _fromInstanceWriter, _toInstanceReader);
                    server.OverrideMainLoop(gameLoop);

                    server.MainLoop();
                }
                catch (Exception e)
                {
                    _fromInstanceWriter.TryWrite(new ShutDownMessage(e));
                    return;
                }

                _fromInstanceWriter.TryWrite(new ShutDownMessage(null));
            }

            private sealed class CommandLineArgs : ICommandLineArgs
            {
                public bool Parse(string[] args)
                {
                    // No-op.
                    return true;
                }

                public string ConfigFile => null;
                public string DataDir => null;
            }
        }

        public sealed class ClientIntegrationInstance : IntegrationInstance
        {
            private readonly ClientIntegrationOptions _options;

            internal ClientIntegrationInstance(ClientIntegrationOptions options)
            {
                _options = options;
                InstanceThread = new Thread(_clientMain) {Name = "Client Instance Thread"};
                DependencyCollection = new DependencyCollection();
                InstanceThread.Start();
            }

            /// <summary>
            ///     Wire up the server to connect to when <see cref="IClientNetManager.ClientConnect"/> gets called.
            /// </summary>
            public void SetConnectTarget(ServerIntegrationInstance server)
            {
                var clientNetManager = ResolveDependency<IntegrationNetManager>();
                var serverNetManager = server.ResolveDependency<IntegrationNetManager>();

                if (!serverNetManager.IsRunning)
                {
                    throw new InvalidOperationException("Server net manager is not running!");
                }

                clientNetManager.NextConnectChannel = serverNetManager.MessageChannelWriter;
            }

            private void _clientMain()
            {
                try
                {
                    IoCManager.InitThread(DependencyCollection);
                    ClientIoC.RegisterIoC(GameController.DisplayMode.Headless);
                    IoCManager.Register<INetManager, IntegrationNetManager>(true);
                    IoCManager.Register<IClientNetManager, IntegrationNetManager>(true);
                    IoCManager.Register<IntegrationNetManager, IntegrationNetManager>(true);
                    _options?.InitIoC?.Invoke();
                    IoCManager.BuildGraph();

                    GameController.RegisterReflection();

                    var client = DependencyCollection.Resolve<IGameControllerInternal>();

                    client.ContentRootDir = "../../";
                    client.LoadConfigAndUserData = false;
                    _options?.BeforeStart?.Invoke();
                    client.Startup();

                    var gameLoop = new IntegrationGameLoop(DependencyCollection.Resolve<IGameTiming>(),
                        _fromInstanceWriter, _toInstanceReader);
                    client.OverrideMainLoop(gameLoop);
                    client.MainLoop(GameController.DisplayMode.Headless);
                }
                catch (Exception e)
                {
                    _fromInstanceWriter.TryWrite(new ShutDownMessage(e));
                    return;
                }

                _fromInstanceWriter.TryWrite(new ShutDownMessage(null));
            }
        }

        // Synchronization between the integration instance and the main loop is done purely through message passing.
        // The main thread sends commands like "run n ticks" and the main loop reports back the commands it has finished.
        // It also reports when it dies, of course.

        internal sealed class IntegrationGameLoop : IGameLoop
        {
            private readonly IGameTiming _gameTiming;

            private readonly ChannelWriter<object> _channelWriter;
            private readonly ChannelReader<object> _channelReader;

#pragma warning disable 67
            public event EventHandler<FrameEventArgs> Input;
            public event EventHandler<FrameEventArgs> Tick;
            public event EventHandler<FrameEventArgs> Update;
            public event EventHandler<FrameEventArgs> Render;
#pragma warning restore 67

            public bool SingleStep { get; set; }
            public bool Running { get; set; }
            public int MaxQueuedTicks { get; set; }
            public SleepMode SleepMode { get; set; }

            public IntegrationGameLoop(IGameTiming gameTiming, ChannelWriter<object> channelWriter,
                ChannelReader<object> channelReader)
            {
                _gameTiming = gameTiming;
                _channelWriter = channelWriter;
                _channelReader = channelReader;
            }

            public void Run()
            {
                // Ack tick message 1 is implied as "init done"
                _channelWriter.TryWrite(new AckTicksMessage(1));
                Running = true;

                Tick += (a, b) => Console.WriteLine("tick: {0}", _gameTiming.CurTick);

                _gameTiming.InSimulation = true;

                while (Running)
                {
                    var message = _channelReader.ReadAsync().AsTask().Result;

                    switch (message)
                    {
                        case RunTicksMessage msg:
                            _gameTiming.InSimulation = true;
                            var simFrameEvent = new FrameEventArgs(msg.Delta);
                            for (var i = 0; i < msg.Ticks && Running; i++)
                            {
                                _gameTiming.CurTick = new GameTick(_gameTiming.CurTick.Value + 1);
                                Tick?.Invoke(this, simFrameEvent);
                            }

                            _channelWriter.TryWrite(new AckTicksMessage(msg.MessageId));
                            break;

                        case StopMessage _:
                            Running = false;
                            break;

                        case PostMessage postMessage:
                            postMessage.Post();
                            _channelWriter.TryWrite(new AckTicksMessage(postMessage.MessageId));
                            break;
                    }
                }
            }
        }

        public class ServerIntegrationOptions : IntegrationOptions
        {
        }

        public class ClientIntegrationOptions : IntegrationOptions
        {
        }

        public abstract class IntegrationOptions
        {
            public Action InitIoC { get; set; }
            public Action BeforeStart { get; set; }
        }

        /// <summary>
        ///     Sent head -> instance to tell the instance to run a few simulation ticks.
        /// </summary>
        private sealed class RunTicksMessage
        {
            public RunTicksMessage(int ticks, float delta, int messageId)
            {
                Ticks = ticks;
                Delta = delta;
                MessageId = messageId;
            }

            public int Ticks { get; }
            public float Delta { get; }
            public int MessageId { get; }
        }

        /// <summary>
        ///     Sent head -> instance to tell the instance to shut down cleanly.
        /// </summary>
        private sealed class StopMessage
        {
        }

        /// <summary>
        ///     Sent instance -> head to confirm finishing of ticks message.
        /// </summary>
        private sealed class AckTicksMessage
        {
            public AckTicksMessage(int messageId)
            {
                MessageId = messageId;
            }

            public int MessageId { get; }
        }

        /// <summary>
        ///     Sent instance -> head when instance shuts down for whatever reason.
        /// </summary>
        private sealed class ShutDownMessage
        {
            public ShutDownMessage(Exception unhandledException)
            {
                UnhandledException = unhandledException;
            }

            public Exception UnhandledException { get; }
        }

        private sealed class PostMessage
        {
            public Action Post { get; }
            public int MessageId { get; }

            public PostMessage(Action post, int messageId)
            {
                Post = post;
                MessageId = messageId;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using ZeroMQ.lib;

namespace ZeroMQ.Monitoring
{

	/// <summary>
	/// Monitors state change events on another socket within the same context.
	/// </summary>
	public class ZMonitor
	{
		/// <summary>
		/// The polling interval in milliseconds.
		/// </summary>
		private const int PollingIntervalMsec = 500;

		private ZSocket _socket;

		private readonly string _endpoint;

		private readonly Dictionary<ZMonitorEvents, Action<ZMonitorEventData>> _eventHandler;

		private volatile bool _isRunning;

		private bool _disposed;

		protected ZMonitor(ZContext context, ZSocket socket, string endpoint)
		{
			_socket = socket;
			_endpoint = endpoint;
			_eventHandler = new Dictionary<ZMonitorEvents, Action<ZMonitorEventData>>
			{
				{ ZMonitorEvents.AllEvents, data => InvokeEvent(AllEvents, () => new ZMonitorEventArgs(this, data)) },
				{ ZMonitorEvents.Connected, data => InvokeEvent(Connected, () => new ZMonitorFileDescriptorEventArgs(this, data)) },
				{ ZMonitorEvents.ConnectDelayed, data => InvokeEvent(ConnectDelayed, () => new ZMonitorEventArgs(this, data)) },
				{ ZMonitorEvents.ConnectRetried, data => InvokeEvent(ConnectRetried, () => new ZMonitorIntervalEventArgs(this, data)) },
				{ ZMonitorEvents.Listening, data => InvokeEvent(Listening, () => new ZMonitorFileDescriptorEventArgs(this, data)) },
				{ ZMonitorEvents.BindFailed, data => InvokeEvent(BindFailed, () => new ZMonitorEventArgs(this, data)) },
				{ ZMonitorEvents.Accepted, data => InvokeEvent(Accepted, () => new ZMonitorFileDescriptorEventArgs(this, data)) },
				{ ZMonitorEvents.AcceptFailed, data => InvokeEvent(AcceptFailed, () => new ZMonitorEventArgs(this, data)) },
				{ ZMonitorEvents.Closed, data => InvokeEvent(Closed, () => new ZMonitorFileDescriptorEventArgs(this, data)) },
				{ ZMonitorEvents.CloseFailed, data => InvokeEvent(CloseFailed, () => new ZMonitorEventArgs(this, data)) },
				{ ZMonitorEvents.Disconnected, data => InvokeEvent(Disconnected, () => new ZMonitorFileDescriptorEventArgs(this, data)) },
				{ ZMonitorEvents.Stopped, data => InvokeEvent(Stopped, () => new ZMonitorEventArgs(this, data)) },
			};
		}

		public static ZMonitor Create(ZContext context, string endpoint)
		{
			ZError error;
			ZMonitor monitor;
			if (null == (monitor = ZMonitor.Create(context, endpoint, out error)))
			{
				throw new ZException(error);
			}
			return monitor;
		}

		/// <summary>
		/// Create a socket with the current context and the specified socket type.
		/// </summary>
		/// <param name="socketType">A <see cref="ZSocketType"/> value for the socket.</param>
		/// <returns>A <see cref="ZSocket"/> instance with the current context and the specified socket type.</returns>
		public static ZMonitor Create(ZContext context, string endpoint, out ZError error)
		{
			ZSocket socket;
			if (null == (socket = ZSocket.Create(context, ZSocketType.PAIR, out error)))
			{
				return default(ZMonitor);
			}

			return new ZMonitor(context, socket, endpoint);
		}

		public event EventHandler<ZMonitorEventArgs> AllEvents;

		/// <summary>
		/// Occurs when a new connection is established.
		/// NOTE: Do not rely on the <see cref="ZMonitorEventArgs.Address"/> value for
		/// 'Connected' messages, as the memory address contained in the message may no longer
		/// point to the correct value.
		/// </summary>
		public event EventHandler<ZMonitorFileDescriptorEventArgs> Connected;

		/// <summary>
		/// Occurs when a synchronous connection attempt failed, and its completion is being polled for.
		/// </summary>
		public event EventHandler<ZMonitorEventArgs> ConnectDelayed;

		/// <summary>
		/// Occurs when an asynchronous connect / reconnection attempt is being handled by a reconnect timer.
		/// </summary>
		public event EventHandler<ZMonitorIntervalEventArgs> ConnectRetried;

		/// <summary>
		/// Occurs when a socket is bound to an address and is ready to accept connections.
		/// </summary>
		public event EventHandler<ZMonitorFileDescriptorEventArgs> Listening;

		/// <summary>
		/// Occurs when a socket could not bind to an address.
		/// </summary>
		public event EventHandler<ZMonitorEventArgs> BindFailed;

		/// <summary>
		/// Occurs when a connection from a remote peer has been established with a socket's listen address.
		/// </summary>
		public event EventHandler<ZMonitorFileDescriptorEventArgs> Accepted;

		/// <summary>
		/// Occurs when a connection attempt to a socket's bound address fails.
		/// </summary>
		public event EventHandler<ZMonitorEventArgs> AcceptFailed;

		/// <summary>
		/// Occurs when a connection was closed.
		/// NOTE: Do not rely on the <see cref="ZMonitorEventArgs.Address"/> value for
		/// 'Closed' messages, as the memory address contained in the message may no longer
		/// point to the correct value.
		/// </summary>
		public event EventHandler<ZMonitorFileDescriptorEventArgs> Closed;

		/// <summary>
		/// Occurs when a connection couldn't be closed.
		/// </summary>
		public event EventHandler<ZMonitorEventArgs> CloseFailed;

		/// <summary>
		/// Occurs when the stream engine (tcp and ipc specific) detects a corrupted / broken session.
		/// </summary>
		public event EventHandler<ZMonitorFileDescriptorEventArgs> Disconnected;

		/// <summary>
		/// Monitoring on this socket ended.
		/// </summary>
		public event EventHandler<ZMonitorEventArgs> Stopped;

		/// <summary>
		/// Gets the endpoint to which the monitor socket is connected.
		/// </summary>
		public string Endpoint
		{
			get { return _endpoint; }
		}

		/// <summary>
		/// Gets a value indicating whether the monitor loop is running.
		/// </summary>
		public bool IsRunning
		{
			get { return _isRunning; }
			private set { _isRunning = value; }
		}

		// private static readonly int sizeof_MonitorEventData = Marshal.SizeOf(typeof(ZMonitorEventData));

		/// <summary>
		/// Begins monitoring for state changes, raising the appropriate events as they arrive.
		/// </summary>
		/// <remarks>NOTE: This is a blocking method and should be run from another thread.</remarks>
		public void Run(CancellationToken cancellus)
		{
			IsRunning = true;

			var poller = ZPollItem.CreateReceiver(_socket);

			_socket.Connect(_endpoint);

			ZError error;
			ZMessage incoming;

			while (IsRunning && !cancellus.IsCancellationRequested)
			{
				if (!poller.PollIn(out incoming, out error, TimeSpan.FromMilliseconds(64)))
				{
					if (error == ZError.EAGAIN)
					{
						error = ZError.None;
						Thread.Sleep(1);

						continue;
					}

					IsRunning = false;
					return;
				}

				var eventValue = new ZMonitorEventData();

				using (incoming) {
					if (incoming.Count > 0) {
						eventValue.Event = (ZMonitorEvents)incoming[0].ReadInt16();
						eventValue.EventValue = incoming[0].ReadInt32();
					}

					if (incoming.Count > 1) {
						eventValue.Address = incoming[1].ReadString();
					}
				}

				OnMonitor(eventValue);
			}

			if (!_socket.Disconnect(_endpoint, out error)) { }
		}

		internal void OnMonitor(ZMonitorEventData data)
		{
			if (_eventHandler.ContainsKey(ZMonitorEvents.AllEvents))
			{
				_eventHandler[ZMonitorEvents.AllEvents](data);
			}
			if (_eventHandler.ContainsKey(data.Event))
			{
				_eventHandler[data.Event](data);
			}
		}

		public void Stop()
		{
			IsRunning = false;
		}

		/// <summary>
		/// Releases the unmanaged resources used by the <see cref="ZMonitor"/>, and optionally disposes of the managed resources.
		/// </summary>
		/// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					Stop();

					if (_socket != null)
					{
						_socket.Dispose();
						_socket = null;
					}
				}
			}
			_disposed = true;
		}

		private void InvokeEvent<T>(EventHandler<T> handler, Func<T> createEventArgs) where T : EventArgs
		{
			if (handler != null)
			{
				handler(this, createEventArgs());
			}
		}
	}
}
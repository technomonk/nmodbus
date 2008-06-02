﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using log4net;
using Modbus.IO;
using Modbus.Message;
using Unme.Common;

namespace Modbus.Device
{
	internal class ModbusMasterTcpConnection : IDisposable
	{
		/// <summary>
		/// Occurs when a Modbus master TCP connection is closed.
		/// </summary>
		public event EventHandler<TcpConnectionEventArgs>  ModbusMasterTcpConnectionClosed;

		private static int instanceCounter;

		private readonly ILog _log = LogManager.GetLogger(Assembly.GetCallingAssembly(), 
			String.Format(CultureInfo.InvariantCulture, "{0}.Instance{1}", typeof(ModbusMasterTcpConnection).FullName, Interlocked.Add(ref instanceCounter, 1)));
		private readonly Func<TcpClient, string> _endPointConverter = FunctionalUtility.Memoize<TcpClient, string>(client => client.Client.RemoteEndPoint.ToString());
		private readonly Func<TcpClient, Stream> _streamConverter = FunctionalUtility.Memoize<TcpClient, Stream>(client => client.GetStream());
		private TcpClient _client;
		private ModbusTcpSlave _slave;
		private byte[] _mbapHeader = new byte[6];
		private byte[] _messageFrame;

		public ModbusMasterTcpConnection(TcpClient client, ModbusTcpSlave slave)
		{
			if (client == null)
				throw new ArgumentNullException("client");
			if (slave == null)
				throw new ArgumentException("slave");

			_client = client;
			_slave = slave;
			_log.DebugFormat("Creating new Master connection at IP:{0}", EndPoint);

			_log.Debug("Begin reading header.");
			Stream.BeginRead(_mbapHeader, 0, 6, ReadHeaderCompleted, null);
		}

		public string EndPoint
		{
			get
			{
				return _endPointConverter.Invoke(_client);
			}
		}

		public Stream Stream
		{
			get
			{
				return _streamConverter.Invoke(_client);
			}
		}

		public TcpClient TcpClient
		{
			get
			{
				return _client;
			}
		}

		public void Dispose()
		{
			DisposableUtility.Dispose(ref _client);
		}

		internal void ReadHeaderCompleted(IAsyncResult ar)
		{
			_log.Debug("Read header completed.");

			CatchExceptionAndRemoveMasterEndPoint(() =>
			{
				// this is the normal way a master closes its connection
				if (Stream.EndRead(ar) == 0)
				{
					_log.Debug("0 bytes read, Master has closed Socket connection.");
					ModbusMasterTcpConnectionClosed.Raise(this, new TcpConnectionEventArgs(EndPoint));
					return;
				}

				_log.DebugFormat("MBAP header: {0}", _mbapHeader.Join(", "));
				ushort frameLength = (ushort) (IPAddress.HostToNetworkOrder(BitConverter.ToInt16(_mbapHeader, 4)));
				_log.DebugFormat("{0} bytes in PDU.", frameLength);
				_messageFrame = new byte[frameLength];

				Stream.BeginRead(_messageFrame, 0, frameLength, ReadFrameCompleted, null);
			}, EndPoint);
		}

		internal void ReadFrameCompleted(IAsyncResult ar)
		{
			CatchExceptionAndRemoveMasterEndPoint(() =>
			{
				_log.DebugFormat("Read Frame completed {0} bytes", Stream.EndRead(ar));
				byte[] frame = _mbapHeader.Concat(_messageFrame).ToArray();
				_log.InfoFormat("RX: {0}", frame.Join(", "));

				IModbusMessage request = ModbusMessageFactory.CreateModbusRequest(frame.Slice(6, frame.Length - 6).ToArray());
				request.TransactionID = (ushort) IPAddress.NetworkToHostOrder(BitConverter.ToInt16(frame, 0));

				// TODO refactor
				ModbusTcpTransport transport = new ModbusTcpTransport();
				// perform action and build response
				IModbusMessage response = _slave.ApplyRequest(request);
				response.TransactionID = request.TransactionID;

				// write response
				byte[] responseFrame = transport.BuildMessageFrame(response);
				_log.InfoFormat("TX: {0}", responseFrame.Join(", "));
				Stream.BeginWrite(responseFrame, 0, responseFrame.Length, WriteCompleted, null);
			}, EndPoint);
		}

		internal void WriteCompleted(IAsyncResult ar)
		{
			_log.Debug("End write.");

			CatchExceptionAndRemoveMasterEndPoint(() =>
			{
				Stream.EndWrite(ar);
				_log.Debug("Begin reading another request.");
				Stream.BeginRead(_mbapHeader, 0, 6, ReadHeaderCompleted, null);
			}, EndPoint);
		}

		/// <summary>
		/// Catches all exceptions thrown when action is executed and removes the master end point.
		/// The exception is ignored when it simply signals a master closing its connection.
		/// </summary>
		internal void CatchExceptionAndRemoveMasterEndPoint(Action action, string endPoint)
		{
			if (action == null)
				throw new ArgumentNullException("action");
			if (endPoint == null)
				throw new ArgumentNullException("endPoint");

			try
			{
				action.Invoke();
			}
			catch (IOException ioe)
			{
				_log.DebugFormat("IOException encountered in ReadHeaderCompleted - {0}", ioe.Message);
				ModbusMasterTcpConnectionClosed.Raise(this, new TcpConnectionEventArgs(EndPoint));

				SocketException socketException = ioe.InnerException as SocketException;
				if (socketException != null && socketException.ErrorCode == Modbus.ConnectionResetByPeer)
				{
					_log.Debug("Socket Exceptiong ConnectionResetByPeer, Master closed connection.");
					return;
				}

				throw;
			}
			catch (Exception e)
			{
				_log.Error("Unexpected exception encountered", e);
				throw;
			}
		}
	}
}

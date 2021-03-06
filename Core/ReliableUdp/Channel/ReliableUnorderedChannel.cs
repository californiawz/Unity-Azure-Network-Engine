﻿using System;
using System.Collections.Generic;
using System.Threading;

using ReliableUdp.Const;
using ReliableUdp.Enums;
using ReliableUdp.Packet;

using ReliableUdp.Utility;

namespace ReliableUdp.Channel
{
    public class ReliableUnorderedChannel : IReliableChannel
	{
		private readonly Queue<UdpPacket> outgoingPackets;
		private readonly bool[] outgoingAcks;
		private readonly PendingPacket[] pendingPackets;
		private readonly bool[] earlyReceived;

		private SequenceNumber localSeqence = new SequenceNumber(0);
		private SequenceNumber remoteSequence = new SequenceNumber(0);
		private SequenceNumber localWindowStart = new SequenceNumber(0);
		private SequenceNumber remoteWindowStart = new SequenceNumber(0);

		private UdpPeer peer;
		private bool mustSendAcks;

		private readonly int windowSize;
		private const int BITS_IN_BYTE = 8;

		private int queueIndex;

		public int PacketsInQueue
		{
			get { return this.outgoingPackets.Count; }
		}

		public ReliableUnorderedChannel(int windowSize)
		{
			this.windowSize = windowSize;

			this.outgoingPackets = new Queue<UdpPacket>(this.windowSize);

			this.outgoingAcks = new bool[this.windowSize];
			this.pendingPackets = new PendingPacket[this.windowSize];
			for (int i = 0; i < this.pendingPackets.Length; i++)
			{
				this.pendingPackets[i] = new PendingPacket();
			}

			this.earlyReceived = new bool[this.windowSize];
		}

		public void Initialize(UdpPeer peer)
		{
			this.peer = peer;
		}

		public void ProcessAck(UdpPacket packet)
		{
			int validPacketSize = (this.windowSize - 1) / BITS_IN_BYTE + 1 + HeaderSize.SEQUENCED;
			if (packet.Size != validPacketSize)
			{
#if UDP_DEBUGGING
				System.Diagnostics.Debug.WriteLine("Invalid Ack Packet Size.");
#endif
                return;
			}

			if (!packet.Sequence.IsValid)
			{
#if UDP_DEBUGGING
				System.Diagnostics.Debug.WriteLine("Sequence is Invalid.");
#endif
				return;
			}

			if ((packet.Sequence - this.localWindowStart).Value <= -this.windowSize)
			{
#if UDP_DEBUGGING
				System.Diagnostics.Debug.WriteLine("Old Acks.");
#endif
				return;
			}

			byte[] acksData = packet.RawData;
#if UDP_DEBUGGING
			System.Diagnostics.Debug.WriteLine($"Acks beginning {packet.Sequence.Value}");
#endif
			int startByte = HeaderSize.SEQUENCED;

			Monitor.Enter(this.pendingPackets);
			for (int i = 0; i < this.windowSize; i++)
			{
				ushort ackSequenceValue = (ushort)((packet.Sequence.Value + i) % SequenceNumber.MAX_SEQUENCE);
				SequenceNumber ackSequence = new SequenceNumber(ackSequenceValue);
				if ((ackSequence - this.localWindowStart).Value < 0)
				{
					continue;
				}

				int currentByte = startByte + i / BITS_IN_BYTE;
				int currentBit = i % BITS_IN_BYTE;

				if ((acksData[currentByte] & (1 << currentBit)) == 0)
				{
					continue;
				}

				if (ackSequence == this.localWindowStart)
				{
					this.localWindowStart++;
				}

				UdpPacket removed = this.pendingPackets[ackSequence.Value % this.windowSize].GetAndClear();
				if (removed != null)
				{
					this.peer.AddIncomingAck(removed, ChannelType.Reliable);
#if UDP_DEBUGGING
					System.Diagnostics.Debug.WriteLine($"Removing reliableInOrder ack: {ackSequence.Value} - true.");
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"Removing reliableInOrder ack: {ackSequence.Value} - false.");
#endif
				}
			}
			Monitor.Exit(this.pendingPackets);
		}

		public void AddToQueue(UdpPacket packet)
		{
			lock (this.outgoingPackets)
			{
				this.outgoingPackets.Enqueue(packet);
			}
		}

		private void ProcessQueuedPackets()
		{
			while (this.outgoingPackets.Count > 0)
			{
				var relate = this.localSeqence - this.localWindowStart;
				if (relate.Value < this.windowSize)
				{
					UdpPacket packet;
					lock (this.outgoingPackets)
					{
						packet = this.outgoingPackets.Dequeue();
					}
					packet.Sequence = this.localSeqence;
					this.pendingPackets[this.localSeqence.Value % this.windowSize].Packet = packet;
					this.localSeqence++;
				}
				else
				{
					break;
				}
			}
		}

		public bool SendNextPacket()
		{
			DateTime currentTime = DateTime.UtcNow;

			Monitor.Enter(this.pendingPackets);
			ProcessQueuedPackets();

			PendingPacket currentPacket;
			bool packetFound = false;
			int startQueueIndex = this.queueIndex;
			do
			{
				currentPacket = this.pendingPackets[this.queueIndex];
				if (currentPacket.Packet != null)
				{
					if (currentPacket.TimeStamp.HasValue)
					{
						double packetHoldTime = (currentTime - currentPacket.TimeStamp.Value).TotalMilliseconds;
						if (packetHoldTime > this.peer.NetworkStatisticManagement.ResendDelay)
						{
#if UDP_DEBUGGING
							System.Diagnostics.Debug.WriteLine($"Resend: {(int)packetHoldTime} > {this.peer.NetworkStatisticManagement.ResendDelay}.");
#endif
							packetFound = true;
						}
					}
					else
					{
						packetFound = true;
					}
				}

				this.queueIndex = (this.queueIndex + 1) % this.windowSize;
			} while (!packetFound && this.queueIndex != startQueueIndex);

			if (packetFound)
			{
				currentPacket.TimeStamp = DateTime.UtcNow;
				this.peer.SendRawData(currentPacket.Packet);
#if UDP_DEBUGGING
				System.Diagnostics.Debug.WriteLine($"Sended.");
#endif
			}
			Monitor.Exit(this.pendingPackets);
			return packetFound;
		}

		public void SendAcks()
		{
			if (!this.mustSendAcks)
				return;
			this.mustSendAcks = false;

#if UDP_DEBUGGING
            System.Diagnostics.Debug.WriteLine($"Send Acks.");
#endif

            int bytesCount = (this.windowSize - 1) / BITS_IN_BYTE + 1;
			PacketType packetType = PacketType.AckReliable;
			var acksPacket = this.peer.GetPacketFromPool(packetType, bytesCount);

			byte[] data = acksPacket.RawData;

			Monitor.Enter(this.outgoingAcks);
			acksPacket.Sequence = this.remoteWindowStart;

			int startAckIndex = this.remoteWindowStart.Value % this.windowSize;
			int currentAckIndex = startAckIndex;
			int currentBit = 0;
			int currentByte = HeaderSize.SEQUENCED;
			do
			{
				if (this.outgoingAcks[currentAckIndex])
				{
					data[currentByte] |= (byte)(1 << currentBit);
				}

				currentBit++;
				if (currentBit == BITS_IN_BYTE)
				{
					currentByte++;
					currentBit = 0;
				}
				currentAckIndex = (currentAckIndex + 1) % this.windowSize;
			} while (currentAckIndex != startAckIndex);
			Monitor.Exit(this.outgoingAcks);

			this.peer.SendRawData(acksPacket);
			this.peer.Recycle(acksPacket);
		}

		public void ProcessPacket(UdpPacket packet)
		{
			if (!packet.Sequence.IsValid)
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Bad Sequence.");
#endif
                return;
			}

			SequenceNumber relate = packet.Sequence - this.remoteWindowStart;
			SequenceNumber relateSeq = packet.Sequence - this.remoteSequence;

			if (relateSeq.Value > this.windowSize)
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Bad Sequence for window size.");
#endif
                return;
			}

			if (relate.Value < 0)
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Reliable in order too old.");
#endif
                return;
			}
			if (relate.Value >= this.windowSize * 2)
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Reliable in order too new.");
#endif
                return;
			}

			Monitor.Enter(this.outgoingAcks);
			if (relate.Value >= this.windowSize)
			{
				int newWindowStart = (this.remoteWindowStart.Value + relate.Value - this.windowSize + 1) % SequenceNumber.MAX_SEQUENCE;

				while (this.remoteWindowStart.Value != newWindowStart)
				{
					this.outgoingAcks[this.remoteWindowStart.Value % this.windowSize] = false;
					this.remoteWindowStart++;
				}
			}

			this.mustSendAcks = true;

			if (this.outgoingAcks[packet.Sequence.Value % this.windowSize])
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Reliable in order duplicate.");
#endif
                Monitor.Exit(this.outgoingAcks);
				return;
			}

			this.outgoingAcks[packet.Sequence.Value % this.windowSize] = true;
			Monitor.Exit(this.outgoingAcks);

			if (packet.Sequence == this.remoteSequence)
			{
#if UDP_DEBUGGING
                System.Diagnostics.Debug.WriteLine("Reliable in order packet success.");
#endif
                this.peer.AddIncomingPacket(packet, ChannelType.Reliable);
				this.remoteSequence++;

				while (this.earlyReceived[this.remoteSequence.Value % this.windowSize])
				{
					this.earlyReceived[this.remoteSequence.Value % this.windowSize] = false;
					this.remoteSequence++;
				}

				return;
			}

			this.earlyReceived[packet.Sequence.Value % this.windowSize] = true;
			this.peer.AddIncomingPacket(packet, ChannelType.Reliable);
		}
	}
}

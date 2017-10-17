﻿/* Copyright 2017 Cinegy GmbH.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cinegy.TsDecoder.TransportStream
{
    public class TsPacketFactory
    {
        private const byte SyncByte = 0x47;
        
        private ulong _lastPcr;

        private byte[] _residualData;

        public readonly int TsPacketFixedSize = 188;

        public TsPacketFactory()
        {

        }

        public TsPacketFactory(byte TsPacketSize)
        {
            TsPacketFixedSize = TsPacketSize;
        }

        /// <summary>
        /// Returns TsPackets for any input data. If data ends with incomplete packet, this is stored and prepended to next call. 
        /// If data stream is restarted, prior buffer will be skipped as sync will not be acknowledged - but any restarts should being with first byte as sync to avoid possible merging with prior data if lengths coincide.
        /// </summary>
        /// <param name="data">Aligned or unaligned data buffer containing TS packets. Aligned is more efficient if possible.</param>
        /// <param name="retainPayload">Optional parameter to trigger any resulting TS payload to be copied into the returned structure</param>
        /// <param name="preserveSourceData">Optional parameter to trigger complete copy of source data for TS packet to be held in array for quick access</param>
        /// <returns>Complete TS packets from this data and any prior partial data rolled over.</returns>
        public TsPacket[] GetTsPacketsFromData(byte[] data, bool retainPayload = true, bool preserveSourceData = false)
        {
            try
            {
                if (_residualData != null)
                {
                    var tempArray = new byte[data.Length];
                    Buffer.BlockCopy(data,0,tempArray,0,data.Length);
                    data = new byte[_residualData.Length + tempArray.Length];
                    Buffer.BlockCopy(_residualData,0,data,0,_residualData.Length);
                    Buffer.BlockCopy(tempArray,0,data,_residualData.Length,tempArray.Length);
                }

                var maxPackets = (data.Length) / TsPacketFixedSize;
                var tsPackets = new TsPacket[maxPackets];

                var packetCounter = 0;

                var start = FindSync(data, 0, TsPacketFixedSize);

                while (start >= 0 && ((data.Length - start) >= TsPacketFixedSize))
                {
                    var tsPacket = new TsPacket
                    {
                        SyncByte = data[start],
                        Pid = (short)(((data[start + 1] & 0x1F) << 8) + (data[start + 2])),
                        TransportErrorIndicator = (data[start + 1] & 0x80) != 0,
                        PayloadUnitStartIndicator = (data[start + 1] & 0x40) != 0,
                        TransportPriority = (data[start + 1] & 0x20) != 0,
                        ScramblingControl = (short)(data[start + 3] >> 6),
                        AdaptationFieldExists = (data[start + 3] & 0x20) != 0,
                        ContainsPayload = (data[start + 3] & 0x10) != 0,
                        ContinuityCounter = (short)(data[start + 3] & 0xF)
                    };

                    if(preserveSourceData)
                    {
                        tsPacket.SourceData = new byte[TsPacketFixedSize];
                        Buffer.BlockCopy(data, start, tsPacket.SourceData, 0, TsPacketFixedSize);
                    }

                    //skip packets with error indicators or on the null PID
                    if (!tsPacket.TransportErrorIndicator && (tsPacket.Pid != (short)PidType.NullPid))
                    {
                        var payloadOffs = start + 4;
                        var payloadSize = TsPacketFixedSize - 4;

                        if (tsPacket.AdaptationFieldExists)
                        {
                            tsPacket.AdaptationField = new AdaptationField()
                            {
                                FieldSize = data[start + 4],
                                DiscontinuityIndicator = (data[start + 5] & 0x80) != 0,
                                RandomAccessIndicator = (data[start + 5] & 0x40) != 0,
                                ElementaryStreamPriorityIndicator = (data[start + 5] & 0x20) != 0,
                                PcrFlag = (data[start + 5] & 0x10) != 0,
                                OpcrFlag = (data[start + 5] & 0x8) != 0,
                                SplicingPointFlag = (data[start + 5] & 0x4) != 0,
                                TransportPrivateDataFlag = (data[start + 5] & 0x2) != 0,
                                AdaptationFieldExtensionFlag = (data[start + 5] & 0x1) != 0
                            };

                            if (tsPacket.AdaptationField.FieldSize >= payloadSize)
                            {
#if DEBUG
                                Debug.WriteLine("TS packet data adaptationFieldSize >= payloadSize");
#endif
                                return null;
                            }
                            
                            if (tsPacket.AdaptationField.PcrFlag && tsPacket.AdaptationField.FieldSize > 0)
                            {
                                //Packet has PCR
                                tsPacket.AdaptationField.Pcr = (((uint)(data[start + 6]) << 24) +
                                                                ((uint)(data[start + 7] << 16)) +
                                                                ((uint)(data[start + 8] << 8)) + (data[start + 9]));

                                tsPacket.AdaptationField.Pcr <<= 1;

                                if ((data[start + 10] & 0x80) == 1)
                                {
                                    tsPacket.AdaptationField.Pcr |= 1;
                                }

                                tsPacket.AdaptationField.Pcr *= 300;
                                var iLow = (uint)((data[start + 10] & 1) << 8) + data[start + 11];
                                tsPacket.AdaptationField.Pcr += iLow;


                                if (_lastPcr == 0) _lastPcr = tsPacket.AdaptationField.Pcr;
                            }


                            payloadSize -= tsPacket.AdaptationField.FieldSize;
                            payloadOffs += tsPacket.AdaptationField.FieldSize;
                        }

                        if (tsPacket.ContainsPayload && tsPacket.PayloadUnitStartIndicator)
                        {
                            if (payloadOffs > (data.Length - 2) || data[payloadOffs] != 0 || data[payloadOffs + 1] != 0 || data[payloadOffs + 2] != 1)
                            {
#if DEBUG
                                //    Debug.WriteLine("PES syntax error: no PES startcode found, or payload offset exceeds boundary of data");
#endif
                            }
                            else
                            {
                                tsPacket.PesHeader = new PesHdr
                                {
                                    StartCode = (uint)((data[payloadOffs] << 16) + (data[payloadOffs + 1] << 8) + data[payloadOffs + 2]),
                                    StreamId = data[payloadOffs + 3],
                                    PacketLength = (ushort)((data[payloadOffs + 4] << 8) + data[payloadOffs + 5]),
                                    Pts = -1,
                                    Dts = -1
                                };

                                tsPacket.PesHeader.HeaderLength = (byte)tsPacket.PesHeader.PacketLength;

                                var stmrId = tsPacket.PesHeader.StreamId; //just copying to small name to make code less huge and slightly faster...

                                if ((stmrId != (uint)PesStreamTypes.ProgramStreamMap) &&
                                    (stmrId != (uint)PesStreamTypes.PaddingStream) &&
                                    (stmrId != (uint)PesStreamTypes.PrivateStream2) &&
                                    (stmrId != (uint)PesStreamTypes.ECMStream) &&
                                    (stmrId != (uint)PesStreamTypes.EMMStream) &&
                                    (stmrId != (uint)PesStreamTypes.ProgramStreamDirectory) &&
                                    (stmrId != (uint)PesStreamTypes.DSMCCStream) &&
                                    (stmrId != (uint)PesStreamTypes.H2221TypeEStream))
                                {
                                    var ptsDtsFlag = data[payloadOffs + 7] >> 6;

                                    tsPacket.PesHeader.HeaderLength = (byte)(9 + data[payloadOffs + 8]); ;

                                    switch (ptsDtsFlag)
                                    {
                                        case 2:
                                            tsPacket.PesHeader.Pts = Get_TimeStamp(2, data, payloadOffs + 9);
                                            break;
                                        case 3:
                                            tsPacket.PesHeader.Pts = Get_TimeStamp(3, data, payloadOffs + 9);
                                            tsPacket.PesHeader.Dts = Get_TimeStamp(1, data, payloadOffs + 14);
                                            break;
                                        case 1:
                                            throw new Exception("PES Syntax error: pts_dts_flag = 1");
                                    }
                                }

                                tsPacket.PesHeader.Payload = new byte[tsPacket.PesHeader.HeaderLength];
                                Buffer.BlockCopy(data, payloadOffs, tsPacket.PesHeader.Payload, 0, tsPacket.PesHeader.HeaderLength);

                                payloadOffs += tsPacket.PesHeader.HeaderLength;
                                payloadSize -= tsPacket.PesHeader.HeaderLength;
                            }
                        }

                        if (payloadSize > 1 && retainPayload)
                        {
                            tsPacket.Payload = new byte[payloadSize];
                            Buffer.BlockCopy(data, payloadOffs, tsPacket.Payload, 0, payloadSize);
                        }
                    }

                    tsPackets[packetCounter++] = tsPacket;

                    start += TsPacketFixedSize;

                    if (start >= data.Length)
                        break;
                    if (data[start] != SyncByte)
                        break;  // but this is strange!
                }

                if ((start + TsPacketFixedSize) != data.Length)
                {
                    //we have 'residual' data to carry over to next call
                    _residualData = new byte[data.Length - start];
                    Buffer.BlockCopy(data,start,_residualData,0,data.Length-start);
                }

                return tsPackets;
            }

            catch (Exception ex)
            {
                Debug.WriteLine("Exception within GetTsPacketsFromData method: " + ex.Message);
            }

            return null;
        }

        private static long Get_TimeStamp(int code, IList<byte> data, int offs)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (code == 0)
            {
                Debug.WriteLine("Method has been called with incorrect code to match against - check for fault in calling method.");
                throw new Exception("PES Syntax error: 0 value timestamp code check passed in");
            }

            if ((data[offs + 0] >> 4) != code)
                throw new Exception("PES Syntax error: Wrong timestamp code");

            if ((data[offs + 0] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            if ((data[offs + 2] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            if ((data[offs + 4] & 1) != 1)
                throw new Exception("PES Syntax error: Invalid timestamp marker bit");

            long a = (data[offs + 0] >> 1) & 7;
            long b = (data[offs + 1] << 7) | (data[offs + 2] >> 1);
            long c = (data[offs + 3] << 7) | (data[offs + 4] >> 1);

            return (a << 30) | (b << 15) | c;
        }

        public static int FindSync(IList<byte> tsData, int offset, int TsPacketSize)
        {
            if (tsData == null) throw new ArgumentNullException(nameof(tsData));

            //not big enough to be any kind of single TS packet
            if (tsData.Count < 188)
            {
                return -1;
            }

            try
            {
                for (var i = offset; i < tsData.Count; i++)
                {
                    //check to see if we found a sync byte
                    if (tsData[i] != SyncByte) continue;
                    if (i + 1 * TsPacketSize < tsData.Count && tsData[i + 1 * TsPacketSize] != SyncByte) continue;
                    if (i + 2 * TsPacketSize < tsData.Count && tsData[i + 2 * TsPacketSize] != SyncByte) continue;
                    if (i + 3 * TsPacketSize < tsData.Count && tsData[i + 3 * TsPacketSize] != SyncByte) continue;
                    if (i + 4 * TsPacketSize < tsData.Count && tsData[i + 4 * TsPacketSize] != SyncByte) continue;
                    // seems to be ok
                    return i;
                }
                return -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Problem in FindSync algorithm... : ", ex.Message);
                throw;
            }
        }
    }
}

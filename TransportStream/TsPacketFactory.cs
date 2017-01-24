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
        private const int TsPacketSize = 188;
        private ulong _lastPcr;

        private byte[] _residualData;

        private byte[] _buffer;
        private int _bufferPos = 0;
        private int _tsPacketCallbackNumber = 7;

        public TsPacketFactory()
        {
            _buffer = new byte[_tsPacketCallbackNumber+1];
        }

        /// <summary>
        /// Accepts unaligned data to be provided to the packet factory, with a callback firing once the return buffer = TsPacketCallbackNumber full
        /// </summary>
        /// <param name="data">Byte array of Transport Stream packets - may be unaligned, but must be contiguous</param>
        public void GetTsPacketsFromDataAsync(byte[] data)
        {
            var subBufferSize = -TsPacketCallbackNumber*TsPacketSize;
            if (data.Length > subBufferSize)
            {
                //a large buffer has been provided - find sync, and split into sub-arrays and provide 
                var pos = 0;

                while (pos < subBufferSize)
                {
                    var syncLocation = FindSync(data,pos);

                    var subBuffer = new byte[subBufferSize];
                    var packets = GetTsPacketsFromData(subBuffer);
                }
                

                    
                
            }
            
        }

        /// <summary>
        /// Defines the number of transport stream packets to buffer before firing the callback (default = 7)
        /// </summary>
        public int TsPacketCallbackNumber
        {
            get { return _tsPacketCallbackNumber; }
            set
            {
                _tsPacketCallbackNumber = value;
                _buffer = new byte[value+1];
            }
        }

        public TsPacket[] GetTsPacketsFromData(byte[] data)
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

                var maxPackets = (data.Length) / TsPacketSize;
                var tsPackets = new TsPacket[maxPackets];

                var packetCounter = 0;

                var start = FindSync(data, 0);

                while (start >= 0 && ((data.Length - start) >= TsPacketSize))
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

                    //skip packets with error indicators or on the null PID
                    if (!tsPacket.TransportErrorIndicator && (tsPacket.Pid != (short)PidType.NullPid))
                    {
                        var payloadOffs = start + 4;
                        var payloadSize = TsPacketSize - 4;

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

                                var change = ((long)_lastPcr - (long)tsPacket.AdaptationField.Pcr);

                                if ((change) > 2000000)
                                {
                                    Debug.WriteLine("Big PCR change");
                                }
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
                                    StartCode = 0x100 + data[payloadOffs + 3],
                                    Pts = -1,
                                    Dts = -1
                                };

                                var ptsDtsFlag = data[payloadOffs + 7] >> 6;

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

                                //if (tsPacket.AdaptationField.PcrFlag && ptsDtsFlag > 1)
                                //{
                                //    var ts = new TimeSpan((long)(((long)tsPacket.AdaptationField.Pcr - tsPacket.PesHeader.Pts * 300)/2.7));
                                //    Debug.WriteLine($"PCR: {tsPacket.AdaptationField.Pcr}, PTS: {tsPacket.PesHeader.Pts}, Delta = {ts}");
                                //}

                                var pesLength = 9 + data[payloadOffs + 8];
                                tsPacket.PesHeader.Payload = new byte[pesLength];
                                Buffer.BlockCopy(data, payloadOffs, tsPacket.PesHeader.Payload, 0, pesLength);

                                payloadOffs += pesLength;
                                payloadSize -= pesLength;
                            }
                        }

                        if (payloadSize > 1)
                        {
                            tsPacket.Payload = new byte[payloadSize];
                            Buffer.BlockCopy(data, payloadOffs, tsPacket.Payload, 0, payloadSize);
                        }
                    }

                    tsPackets[packetCounter++] = tsPacket;

                    start += TsPacketSize;

                    if (start >= data.Length)
                        break;
                    if (data[start] != SyncByte)
                        break;  // but this is strange!
                }

                if ((start + TsPacketSize) != data.Length)
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

        private static int FindSync(IList<byte> tsData, int offset)
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

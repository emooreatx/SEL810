// SEL810.cs
// Copyright � 2020 Kenneth Gober
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.


using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Emulator
{
    class SEL810
    {
        public const Int32 CORE_SIZE = 32768;       // number of words of memory
        public const Int32 DEFAULT_GUI_PORT = 8100; // TCP port for front panel
        public const Int32 DEFAULT_TTY_PORT = 8101; // TCP port for console TTY

        private static TimeSpan sIndicatorLag = new TimeSpan(0, 0, 0, 0, 200);

        private volatile Boolean vExitGUI = false;
        private volatile Boolean vStep = false;
        private volatile Boolean vHalt = true;
        private volatile Boolean vIOHold = false;
        private volatile Boolean vInterrupt = false;
        private volatile Boolean vOverflow = false;
        private volatile Boolean vBPR = false;
        private volatile Boolean vBPW = false;
        private volatile Boolean vBPA = false;
        private volatile Boolean vBPB = false;
        private volatile Boolean vBPIR = false;
        private volatile Boolean vBPPC = false;

        private Object mLock = new Object();
        private Thread mCPUThread;
        private Thread mGUIThread;
        private Socket mGUISocket;
        private Int32 mGUIProtocol;
        private Int32 mGUIProtocolState;
        private Boolean mGUIDirty;
        private JSON.Value mGUIState;

        private Int16[] mCore = new Int16[CORE_SIZE];
        private Int16 mT, mA, mB, mPC, mIR, mSR, mX, mPPR, mVBR;
        private Boolean mCF, mXP;

        private Int16[] mIntRequest = new Int16[9]; // interrupt request
        private Int16[] mIntEnabled = new Int16[9]; // interrupt enabled
        private Int16[] mIntActive = new Int16[9]; // interrupt active
        private Boolean mTOI, mIntBlocked;
        private Int32 mIntGroup = 8;
        private Int16 mIntLevel = 0;
        private Int16 mIntMask = 0;

        private Int16[] mBPR = new Int16[CORE_SIZE];
        private Int16[] mBPW = new Int16[CORE_SIZE];
        private Boolean[] mBPA = new Boolean[65536];
        private Boolean[] mBPB = new Boolean[65536];
        private Boolean[] mBPIR = new Boolean[65536];
        private Boolean[] mBPPC = new Boolean[32768];

        private IO[] mIO = new IO[64];

        public SEL810(Int32 guiVersion)
        {
            mGUIProtocol = guiVersion;
            if (guiVersion == 1) mGUIState = JSON.Value.ReadFrom(@"{
                ""Program Counter"": 0,
                ""A Register"": 0,
                ""B Register"": 0,
                ""Control Switches"": 0,
                ""Instruction"": 0,
                ""Interrupt Register"": 0,
                ""Transfer Register"": 0,
                ""Protect Register"": 0,
                ""VBR Register"": 0,
                ""Index Register"": 0,
                ""Stall Counter"": 0,
                ""halt"": true,
                ""iowait"": false,
                ""overflow"": false,
                ""master_clear"": false,
                ""parity"": false,
                ""display"": false,
                ""enter"": false,
                ""step"": false,
                ""io_hold_release"": false,
                ""cold_boot"": false,
                ""carry"": false,
                ""protect"": false,
                ""mode_key"": false,
                ""index_pointer"": false,
                ""stall"": false,
                ""assembler"": ""RNA"",
                ""sim_ticks"": 0,
                ""Program Counter_pwm"": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
                ""A Register_pwm"": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
                ""B Register_pwm"": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
                ""Instruction_pwm"": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
                ""Transfer Register_pwm"": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]
            }");
            mGUIThread = new Thread(new ThreadStart(GUIThread));
            mGUIThread.Start();

            mCPUThread = new Thread(new ThreadStart(CPUThread));
            mCPUThread.Start();

            mIO[1] = new Teletype(DEFAULT_TTY_PORT);
        }

        public Boolean IsHalted
        {
            get { return vHalt; }
            set { vHalt = value; }
        }

        public Int16 A
        {
            get { return mA; } // TODO: make thread-safe
            set { mA = value; } // TODO: make thread-safe
        }

        public Int16 B
        {
            get { return mB; } // TODO: make thread-safe
            set { mB = value; } // TODO: make thread-safe
        }

        public Int16 T
        {
            get { return mT; } // TODO: make thread-safe
            set { mT = value; } // TOOD: make thread-safe
        }

        public Int16 PC
        {
            get { return mPC; } // TODO: make thread-safe
            set { mPC = (Int16)(value & 0x7fff); } // TODO: make thread-safe
        }

        public Int16 IR
        {
            get { return mIR; } // TODO: make thread-safe
            set { mIR = value; } // TODO: make thread-safe
        }

        public Int16 SR
        {
            get { return mSR; } // TODO: make thread-safe
            set { mSR = value; } // TODO: make thread-safe
        }

        public Teletype.Mode ConsoleMode
        {
            get { return (mIO[1] as Teletype).OutputMode; } // TODO: make thread-safe
            set { (mIO[1] as Teletype).OutputMode = value; } // TODO: make thread-safe
        }

        public Int16 this[Int32 index]
        {
            get { return mCore[index]; } // TODO: make thread-safe
            set { mCore[index] = value; } // TODO: make thread-safe
        }

        public void MasterClear()
        {
            mT = mB = mA = mIR = mPC = 0;
            mVBR = 0;
            ClearOverflow();
            mCF = false;
        }

        public void Load(Int32 loadAddress, String imageFile)
        {
            Load(loadAddress, File.ReadAllBytes(imageFile));
        }

        public void Load(Int32 loadAddress, Byte[] bytesToLoad)
        {
            Load(loadAddress, bytesToLoad, 0, bytesToLoad.Length);
        }

        public void Load(Int32 loadAddress, Byte[] bytesToLoad, Int32 offset, Int32 count)
        {
            while (count-- > 0)
            {
                Int32 word = bytesToLoad[offset++] << 8;
                if (count-- > 0) word |= bytesToLoad[offset++];
                loadAddress %= CORE_SIZE;
                mCore[loadAddress++] = (Int16)(word);
            }
        }

        public void SetReader(String inputFile)
        {
            Teletype cons = mIO[1] as Teletype;
            cons.SetReader(inputFile);
        }

        public void SetPunch(String outputFile)
        {
            Teletype cons = mIO[1] as Teletype;
            cons.SetPunch(outputFile);
        }

        public void AttachDevice(Int16 unit, String destination)
        {
            IO device;
            lock (mIO)
            {
                device = mIO[unit];
                mIO[unit] = null;
            }
            if (device != null) device.Exit();
            if ((destination == null) || (destination.Length == 0)) return;
            Int32 port;
            Int32 p = destination.IndexOf(':');
            if (p == -1)
            {
                port = 8100 + unit;
            }
            else if (!Int32.TryParse(destination.Substring(p + 1), out port))
            {
                Console.Out.WriteLine("Unrecognized TCP port: {0}", destination.Substring(p + 1));
                return;
            }
            else if ((port < 1) || (port > 65535))
            {
                Console.Out.WriteLine("Unrecognized TCP port: {0}", destination.Substring(p + 1));
                return;
            }
            device = new NetworkDevice(destination, port);
            lock (mIO) mIO[unit] = device;
        }

        public void Run()
        {
            if (vHalt)
            {
                Console.Out.Write("[RUN]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["halt"] = new JSON.Value(false);
                    mGUIDirty = true;
                }
            }
            vHalt = false;
        }

        public void Halt()
        {
            if (!vHalt)
            {
                Console.Out.Write("[HALT]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["halt"] = new JSON.Value(true);
                    mGUIDirty = true;
                }
            }
            vHalt = true;
        }

        public void Step()
        {
            vStep = true;
            while (vStep) Thread.Sleep(50);
        }

        private void SetIOHold()
        {
            if (!vIOHold)
            {
                if (Program.VERBOSE) Console.Out.Write("[+IOH]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["iowait"] = new JSON.Value(true);
                    mGUIDirty = true;
                }
            }
            vIOHold = true;
        }

        public void ReleaseIOHold()
        {
            if (vIOHold)
            {
                if (Program.VERBOSE) Console.Out.Write("[-IOH]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["iowait"] = new JSON.Value(false);
                    mGUIDirty = true;
                }
            }
            vIOHold = false;
        }

        private void SetInterrupt()
        {
            if (!vInterrupt)
            {
                if (Program.VERBOSE) Console.Out.Write("[+INT]");
            }
            vInterrupt = true;
        }

        private void ClearInterrupt()
        {
            if (vInterrupt)
            {
                if (Program.VERBOSE) Console.Out.Write("[-INT]");
            }
            vInterrupt = false;
        }

        private void SetOverflow()
        {
            if (!vOverflow)
            {
                if (Program.VERBOSE) Console.Out.Write("[+OVF]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["overflow"] = new JSON.Value(true);
                    mGUIDirty = true;
                }
            }
            vOverflow = true;
        }

        private void ClearOverflow()
        {
            if (vOverflow)
            {
                if (Program.VERBOSE) Console.Out.Write("[-OVF]");
                if (mGUIProtocol == 1)
                {
                    mGUIState["overflow"] = new JSON.Value(false);
                    mGUIDirty = true;
                }
            }
            vOverflow = false;
        }

        public void Exit()
        {
            lock (mIO)
            {
                for (Int32 i = 0; i < mIO.Length; i++)
                {
                    if (mIO[i] != null) mIO[i].Exit();
                    mIO[i] = null;
                }
            }
            vExitGUI = true;
            mGUIThread.Join();
            mCPUThread.Abort();
            mCPUThread.Join();
        }

        public Int16 GetBPR(Int16 addr)
        {
            if (!vBPR) return 0;
            lock (mBPR) return mBPR[addr];
        }

        public void SetBPR(Int16 addr, Int16 count)
        {
            lock (mBPR)
            {
                mBPR[addr] = count;
                for (Int32 i = 0; i < mBPR.Length; i++)
                {
                    if (mBPR[i] == 0) continue;
                    vBPR = true;
                    return;
                }
                vBPR = false;
            }
        }

        public Int16 GetBPW(Int16 addr)
        {
            if (!vBPR) return 0;
            lock (mBPW) return mBPW[addr];
        }

        public void SetBPW(Int16 addr, Int16 count)
        {
            lock (mBPW)
            {
                mBPW[addr] = count;
                for (Int32 i = 0; i < mBPW.Length; i++)
                {
                    if (mBPW[i] == 0) continue;
                    vBPW = true;
                    return;
                }
                vBPW = false;
            }
        }

        public Boolean GetBPReg(Int32 index, Int32 value)
        {
            switch (index)
            {
                case 0: return mBPA[value];
                case 1: return mBPB[value];
                case 2: return mBPIR[value];
                case 3: return mBPPC[value];
            }
            return false;
        }

        public void SetBPReg(Int32 index, Int32 value)
        {
            switch (index)
            {
                case 0: mBPA[value] = vBPA = true; return;
                case 1: mBPB[value] = vBPB = true; return;
                case 2: mBPIR[value] = vBPIR = true; return;
                case 3: mBPPC[value] = vBPPC = true; return;
            }
        }

        public void ClearBPReg(Int32 index, Int32 value)
        {
            switch (index)
            {
                case 0: mBPA[value] = false; return;
                case 1: mBPB[value] = false; return;
                case 2: mBPIR[value] = false; return;
                case 3: mBPPC[value] = false; return;
            }
        }

        private void RefreshGUI()
        {
            Socket gui = mGUISocket;
            if (gui == null) return;
            if (mGUIProtocol == 1)
            {
                String s = mGUIState.ToString();
                Byte[] buf = Encoding.ASCII.GetBytes(s);
                Byte[] len = new Byte[2];
                Int32 n = buf.Length;
                len[0] = (Byte)((n >> 8) & 255);
                len[1] = (Byte)(n & 255);
                gui.Send(len, 0, 2, SocketFlags.None); // TODO: verify return code
                gui.Send(buf, 0, n, SocketFlags.None); // TODO: verify return code
            }
            mGUIDirty = false;
        }

        private void GUIThread()
        {
            TcpListener L = new TcpListener(IPAddress.Any, DEFAULT_GUI_PORT);
            L.Start();
            while (!vExitGUI)
            {
                while ((!L.Pending()) && (!vExitGUI)) Thread.Sleep(100);
                if (vExitGUI) break;
                TcpClient C = L.AcceptTcpClient();
                mGUIProtocolState = 0;
                lock (this) mGUISocket = C.Client;
                Console.Out.Write("[+GUI]");
                RefreshGUI();
                while ((C.Connected) && (!vExitGUI))
                {
                    Thread.Sleep(200);
                    StepFrontPanel();
                }
                C.Close();
                Console.Out.Write("[-GUI]");
                lock (this) mGUISocket = null;
            }
            L.Stop();
        }

        private void CPUThread()
        {
            while (true)
            {
                while ((vHalt) && (!vStep))
                {
                    Thread.Sleep(100);
                }
                if (vStep)
                {
                    StepCPU();
                    StepInterrupts();
                    vStep = false;
                }
                while (!vHalt)
                {
                    StepCPU();
                    StepInterrupts();
                }
            }
        }

        private void StepCPU()
        {
            // o ooo xim aaa aaa aaa - memory reference instruction
            // o ooo xis sss aaa aaa - augmented instruction
            Int16 r16;
            Int32 r32, ea;
            Boolean i, m;
            Int16 PC_inc = 1;
            Int32 op = (mIR >> 12) & 15;
            if (op == 0) // augmented 00 instructions
            {
                Int32 aug = mIR & 63;
                Int32 sc = (mIR >> 6) & 15;
                switch (aug)
                {
                    case 0: // HLT - halt
                        wIR(Read(mPC));
                        Halt();
                        return;
                    case 1: // 00-01 RNA - round A
                        r16 = mA;
                        if ((mB & 0x4000) != 0) r16++;
                        if ((r16 == 0) && (mA != 0)) SetOverflow();
                        wA(r16);
                        break;
                    case 2: // 00-02 NEG - negate A
                        if (mA == -32768) SetOverflow();
                        wA((Int16)(-mA - ((mCF) ? 1 : 0)));
                        break;
                    case 3: // CLA - clear A
                        wA(0);
                        break;
                    case 4: // TBA - transfer B to A
                        wA(mB);
                        break;
                    case 5: // TAB - transfer A to B
                        wB(mA);
                        break;
                    case 6: // 00-06 IAB - interchange A and B
                        mT = mA;
                        wA(mB);
                        wB(mT);
                        break;
                    case 7: // 00-07 CSB - copy sign of B
                        if (mB < 0)
                        {
                            mCF = true; // TODO: find out exactly which instructions CF affects
                            wB(mB &= 0x7fff); // AMA, SMA and NEG are documented, but what else?
                        }
                        mIntBlocked = true;
                        break;
                    case 8: // RSA - right shift arithmetic
                        wA((Int16)((mA & -32768) | (mA >> sc)));
                        break;
                    case 9: // LSA - left shift arithmetic
                        r16 = (Int16)(mA & 0x7fff);
                        r16 <<= sc;
                        mA &= -32768;
                        wA(mA |= (Int16)(r16 & 0x7fff));
                        break;
                    case 10: // FRA - full right arithmetic shift
                        r32 = (mA << 16) | ((mB & 0x7fff) << 1);
                        r32 >>= sc;
                        wA((Int16)(r32 >> 16));
                        mB &= -32768;
                        wB(mB |= (Int16)((r32 >> 1) & 0x7fff));
                        break;
                    case 11: // FLL - full left logical shift
                        r32 = (mA << 16) | (mB & 0xffff);
                        r32 <<= sc;
                        wA((Int16)((r32 >> 16) & 0xffff));
                        wB((Int16)(r32 & 0xffff));
                        break;
                    case 12: // FRL - full rotate left
                        Int64 r64 = (mA << 16) | (mB & 0xffff);
                        r64 <<= sc;
                        wA((Int16)((r64 >> 16) & 0xffff));
                        mB <<= sc;
                        r64 >>= 32;
                        wB(mB |= (Int16)(r64 & ((1 << sc) - 1)));
                        break;
                    case 13: // RSL - right shift logical
                        r32 = mA & 0xffff;
                        r32 >>= sc;
                        wA((Int16)(r32));
                        break;
                    case 14: // LSL - logical shift left
                        wA(mA <<= sc);
                        break;
                    case 15: // FLA - full left arithmetic shift
                        r32 = (mA << 16) | ((mB & 0x7fff) << 1);
                        r32 <<= sc;
                        mA &= -32768;
                        wA(mA |= (Int16)((r32 >> 16) & 0x7fff));
                        mB &= -32768;
                        wB(mB |= (Int16)((r32 >> 1) & 0x7fff));
                        break;
                    case 16: // ASC - complement sign of accumulator
                        wA(mA ^= -32768);
                        break;
                    case 17: // 00-21 SAS - skip on accumulator sign
                        if (mA > 0) ++mPC;
                        if (mA >= 0) ++mPC;
                        break;
                    case 18: // SAZ - skip if accumulator zero
                        if (mA == 0) ++mPC;
                        break;
                    case 19: // SAN - skip if accumulator negative
                        if (mA < 0) ++mPC;
                        break;
                    case 20: // SAP - skip if accumulator positive
                        if (mA >= 0) ++mPC;
                        break;
                    case 21: // 00-25 SOF - skip if no overflow
                        if (vOverflow) ClearOverflow();
                        else ++mPC;
                        break;
                    case 22: // IBS - increment B and skip
                        wB(++mB);
                        if (mB >= 0) ++mPC;
                        break;
                    case 23: // 00-27 ABA - and B and A accumulators
                        wA((Int16)(mA & mB));
                        break;
                    case 24: // 00-30 OBA - or B and A accumulators
                        wA((Int16)(mA | mB));
                        break;
                    case 25: // 00-31 LCS - load control switches
                        mT = mSR;
                        wA(mT);
                        break;
                    case 26: // 00-32 SNO - skip normalized accumulator
                        if ((mA & 0x8000) != ((mA << 1) & 0x8000)) ++mPC;
                        break;
                    case 27: // NOP - no operation
                        break;
                    case 28: // 00-34 CNS - convert number system
                        if (mA == -32768) SetOverflow();
                        if (mA < 0) wA((Int16)(-mA | -32768));
                        break;
                    case 29: // 00-35 TOI - turn off interrupt
                        mIntBlocked = true;
                        mTOI = true;
                        break;
                    case 30: // 00-36 LOB - long branch
                        wPC(++mPC);
                        mT = Read(mPC);
                        wPC((Int16)(mT & 0x7fff));
                        PC_inc = 0;
                        if (mTOI) DoTOI();
                        break;
                    case 31: // 00-37 OVS - set overflow
                        SetOverflow();
                        break;
                    case 32: // TBP - transfer B to protect register
                        mPPR = mB;
                        break;
                    case 33: // TPB - transfer protect register to B
                        wB(mPPR);
                        break;
                    case 34: // TBV - transfer B to variable base register
                        mVBR = (Int16)(mB & 0x7e00);
                        break;
                    case 35: // TVB - transfer variable base register to B
                        wB(mVBR);
                        break;
                    case 36: // STX - store index
                        i = ((mIR & 0x400) != 0); // I flag
                        m = ((mIR & 0x200) != 0); // M flag
                        wPC(++mPC);
                        if (!i) ea = mPC;
                        else ea = Indirect(mPC, m);
                        Write(ea, mX);
                        break;
                    case 37: // LIX - load index
                        i = ((mIR & 0x400) != 0); // I flag
                        m = ((mIR & 0x200) != 0); // M flag
                        wPC(++mPC);
                        if (!i) ea = mPC;
                        else ea = Indirect(mPC, m);
                        mT = Read(ea);
                        mX = mT;
                        break;
                    case 38: // XPX - set index pointer to index register
                        mXP = true;
                        break;
                    case 39: // XPB - set index pointer to B
                        mXP = false;
                        break;
                    case 40: // 00-50 SXB - skip if index register is B
                        if (!mXP) ++mPC;
                        break;
                    case 41: // 00-N-51 IXS - increment index and skip if positive
                        mX += (Int16)(sc);
                        if (mX >= 0) ++mPC;
                        break;
                    case 42: // TAX - transfer A to index register
                        mX = mA;
                        break;
                    case 43: // TXA - transfer index register to A
                        wA(mX);
                        break;
                    case 44: // RTX
                    default: // TODO: what do undefined opcodes do?
                        break;
                }
            }
            else if (op == 11) // augmented 13 instructions
            {
                Int32 aug = (mIR >> 6) & 7;
                Int32 unit = mIR & 0x3f;
                i = ((mIR & 0x400) != 0); // I flag
                m = ((mIR & 0x200) != 0); // M flag
                switch (aug)
                {
                    case 0: // CEU - command external unit (skip mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        mT = Read(ea);
                        if (IO_Command(unit, mT, false)) ++mPC;
                        break;
                    case 1: // CEU - command external unit (wait mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        mT = Read(ea);
                        IO_Command(unit, mT, true);
                        break;
                    case 2: // TEU - test external unit
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        mT = Read(ea);
                        if (IO_Test(unit, mT)) ++mPC;
                        break;
                    case 4: // 13-04-N SNS - sense numbered switch
                        unit &= 15;
                        mT = mSR;
                        if (((mT << unit) & 0x8000) == 0) ++mPC;
                        break;
                    case 6:
                        if (unit == 0) // 130600 PIE - priority interrupt enable
                        {
                            mT = Read(wPC(++mPC));
                            unit = mT & 0x7000;
                            mIntEnabled[unit] |= (Int16)(mT & 0x0fff);
                            mIntBlocked = true;
                        }
                        else if (unit == 1) // PID - priority interrupt disable
                        {
                            mT = Read(wPC(++mPC));
                            unit = mT & 0x7000;
                            mIntEnabled[unit] &= (Int16)(~(mT & 0x0fff));
                            mIntBlocked = true;
                        }
                        break;
                }
            }
            else if (op == 15) // augmented 17 instructions
            {
                Int32 aug = (mIR >> 6) & 7;
                Int32 unit = mIR & 0x3f;
                Boolean r = ((mIR & 0x800) != 0); // R flag
                i = ((mIR & 0x400) != 0); // I flag
                m = ((mIR & 0x200) != 0); // M flag
                switch (aug)
                {
                    case 0: // AOP - accumulator output to peripheral (skip mode)
                        if (IO_Write(unit, mA, false)) ++mPC;
                        break;
                    case 1: // AOP - accumulator output to peripheral (wait mode)
                        IO_Write(unit, mA, true);
                        break;
                    case 2: // AIP - accumulator input from peripheral (skip mode)
                        if (IO_Read(unit, out r16, false))
                        {
                            if (r) wA((Int16)((mA + r16) & 0xffff));
                            else wA(r16);
                            ++mPC;
                        }
                        break;
                    case 3: // AIP - accumulator input from peripheral (wait mode)
                        IO_Read(unit, out r16, true);
                        if (r) wA((Int16)((mA + r16) & 0xffff));
                        else wA(r16);
                        break;
                    case 4: // MOP - memory output to peripheral (skip mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        mT = Read(ea);
                        if (IO_Write(unit, mT, false)) ++mPC;
                        break;
                    case 5: // MOP - memory output to peripheral (wait mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        mT = Read(ea);
                        IO_Write(unit, mT, true);
                        break;
                    case 6: // MIP - memory input from peripheral (skip mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        if (IO_Read(unit, out r16, false)) ++mPC;
                        Write(ea, r16);
                        break;
                    case 7: // MIP - memory input from peripheral (wait mode)
                        wPC(++mPC);
                        ea = (i) ? Indirect(mPC, m) : mPC;
                        IO_Read(unit, out r16, true);
                        Write(ea, r16);
                        break;
                }
            }
            else
            {
                Boolean x = ((mIR & 0x800) != 0); // X flag
                i = ((mIR & 0x400) != 0); // I flag
                m = ((mIR & 0x200) != 0); // M flag
                ea = mIR & 511; // TODO: should be mT & 511, verify that this works
                if (m) ea |= mPC & 0x7e00;
                if (x) ea += (mXP) ? mX : mB;
                if (!m && !x) ea |= mVBR & 0x7e00;
                while (i)
                {
                    mT = Read(ea);
                    x = ((mT & 0x8000) != 0);
                    i = ((mT & 0x4000) != 0);
                    ea = (mPC & 0x4000) | (mT & 0x3fff);
                    if (x) ea += (mXP) ? mX : mB;
                }
                switch (op)
                {
                    case 1: // 01 LAA - load A accumulator
                        mT = Read(ea);
                        wA(mT);
                        break;
                    case 2: // LBA - load B accumulator
                        mT = Read(ea);
                        wB(mT);
                        break;
                    case 3: // STA - store A accumulator
                        Write(ea, mA);
                        break;
                    case 4: // STB - store B accumulator
                        Write(ea, mB);
                        break;
                    case 5: // 05 AMA - add memory to A
                        mT = Read(ea);
                        r16 = (Int16)(mA + mT + ((mCF) ? 1 : 0));
                        if (((mA & 0x8000) == (mT & 0x8000)) && ((mA & 0x8000) != (r16 & 0x8000))) SetOverflow();
                        wA(r16);
                        break;
                    case 6: // 06 SMA - subtract memory from A
                        mT = Read(ea);
                        r16 = (Int16)(mA - mT - ((mCF) ? 1 : 0));
                        if (((mA & 0x8000) != (mT & 0x8000)) && ((mA & 0x8000) != (r16 & 0x8000))) SetOverflow();
                        wA(r16);
                        break;
                    case 7: // 07 MPY - multiply
                        mT = Read(ea);
                        r32 = mT * mB;
                        if ((mT == -32768) && (mB == -32768)) SetOverflow();
                        wB((Int16)(r32 & 0x7fff));
                        wA((Int16)((r32 >> 15) & 0xffff));
                        break;
                    case 8: // 10 DIV - divide
                        mT = Read(ea);
                        r32 = (mA <<  15) | (mB & 0x7fff);
                        if (mA >= mT) SetOverflow();
                        wB((Int16)(r32 % mT));
                        wA((Int16)(r32 / mT));
                        break;
                    case 9: // 11 BRU - branch unconditional
                        wPC((Int16)(ea));
                        PC_inc = 0;
                        if ((mTOI) && ((mIR & 0x400) != 0)) DoTOI();
                        break;
                    case 10: // 12 SPB - store place and branch
                        Write(ea, (Int16)(++mPC & 0x3fff)); // save only 14 bits, BRU* will see high bits as X=0 I=0
                        wPC((Int16)(ea));
                        mIntBlocked = true;
                        break;
                    case 12: // 14 IMS - increment memory and skip
                        r16 = mT = Read(ea);
                        Write(ea, ++r16);
                        if (r16 == 0) ++mPC;
                        break;
                    case 13: // 15 CMA - compare memory and accumulator
                        mT = Read(ea);
                        if (mA > mT) ++mPC;
                        if (mA >= mT) ++mPC;
                        break;
                    case 14: // 16 AMB - add memory to B
                        mT = Read(ea);
                        r16 = (Int16)(mB + mT + ((mCF) ? 1 : 0));
                        if (((mB & 0x8000) == (mT & 0x8000)) && ((mB & 0x8000) != (r16 & 0x8000))) SetOverflow();
                        wB(r16);
                        break;
                }
            }
            if (mIR != 7) mCF = false;
            if (PC_inc != 0) wPC(mPC += PC_inc);
            mT = Read(mPC);
            wIR(mT);
        }

        private Int16 Indirect(Int16 addr, Boolean M)
        {
            Boolean x, i;
            Int32 ea = addr;
            do
            {
                mT = Read(ea);
                x = ((mT & 0x8000) != 0);
                i = ((mT & 0x4000) != 0);
                ea = mT & 0x3fff;
                if (M) ea |= mPC & 0x4000;
                if (x) ea += (mXP) ? mX : mB;
            }
            while (i);
            return (Int16)(ea);
        }

        private void DoTOI()
        {
            Int16 mask = (Int16)(~mIntMask);
            mIntActive[mIntGroup] &= mask;
            mIntRequest[mIntGroup] &= mask;
            mTOI = false;
            for (Int32 i = 0; i < 8; i++)
            {
                if (mIntActive[i] == 0) continue;
                Int16 A = mIntActive[i];
                mask = 0x800;
                Int16 lev = 1;
                while (mask != 0)
                {
                    if ((A & mask) != 0) break;
                    mask >>= 1;
                    lev++;
                }
                mIntGroup = i;
                mIntLevel = lev;
                mIntMask = mask;
                return;
            }
            mIntGroup = 8;
            mIntLevel = 0;
            mIntMask = 0;
            ClearInterrupt();
        }

        private void StepInterrupts()
        {
            // check for interrupt requests
            for (Int32 unit = 0; unit < mIO.Length; unit++)
            {
                if (mIO[unit] == null) continue;
                Int16[] IRQ = mIO[unit].Interrupts;
                if (IRQ == null) continue;
                for (Int32 g = 0; g < 8; g++) if (IRQ[g] != 0) mIntRequest[g] |= IRQ[g];
            }

            // check whether to trigger an interrupt
            if (mIntBlocked)
            {
                mIntBlocked = false;
            }
            else
            {
                for (Int32 g = 0; g <= mIntGroup; g++)
                {
                    Int16 mask = (Int16)(mIntRequest[g] & mIntEnabled[g]);
                    if (mask == 0) continue;
                    if ((g < mIntGroup) || ((mask & ~mIntMask) > mIntMask))
                    {
                        // set new active interrupt group/level
                        mIntGroup = g;
                        mIntMask = 0x800;
                        while (mIntMask > 0)
                        {
                            if ((mask & mIntMask) != 0) break;
                            mIntMask >>= 1;
                        }
                        mIntActive[mIntGroup] |= mIntMask;

                        // select interrupt vector
                        Int32 ea = 514 + mIntGroup * 16;
                        if (mIntGroup > 2) ea += 16; // skip '1060 range used by BTC
                        mask = mIntMask;
                        mIntLevel = 1;
                        while ((mask & 0x800) == 0)
                        {
                            mIntLevel++;
                            ea++;
                            mask <<= 1;
                        }
                        SetInterrupt();

                        // execute SPB* instruction
                        mT = Read(ea);
                        ea = mT & 0x7fff;
                        Write(ea, mPC);
                        wPC((Int16)(ea + 1));
                        wIR(Read(mPC));
                        mIntBlocked = true;
                        break;
                    }
                }
            }
        }

        private void StepFrontPanel()
        {
            Socket gui = mGUISocket;
            if (gui == null) return;
            if (mGUIDirty) RefreshGUI();
            Int32 n;
            try
            {
                n = gui.Available;
            }
            catch
            {
                return;
            }
            if (mGUIProtocol == 1)
            {
                if (n == 0) return;
                switch (mGUIProtocolState)
                {
                    case 0:
                        if (n < 2) return;
                        Byte[] len = new Byte[2];
                        gui.Receive(len, 0, 2, SocketFlags.None); // TODO: verify return code
                        mGUIProtocolState = len[0]*256 + len[1];
                        break;
                    default:
                        if (n < mGUIProtocolState) return;
                        Byte[] buf = new Byte[mGUIProtocolState];
                        gui.Receive(buf, 0, mGUIProtocolState, SocketFlags.None); // TODO: verify return code
                        String s = Encoding.ASCII.GetString(buf);
                        Console.Out.WriteLine(s);
                        JSON.Value v = JSON.Value.ReadFrom(s);
                        mGUIProtocolState = 0;
                        break;
                }
            }
            else if (n != 0)
            {
                Byte[] buf = new Byte[n];
                gui.Receive(buf, 0, n, SocketFlags.None);
                // discard
            }
        }

        private Int16 wA(Int16 value)
        {
            if (vBPA)
            {
                Int32 p = value & 0x7fff;
                if (value < 0) p += 32768;
                lock (mBPA)
                {
                    if (mBPA[p])
                    {
                        Halt();
                        Console.Out.Write("[A:{0:x4}/{1}]", value, Program.Octal(value, 6));
                    }
                }
            }
            if (mGUIProtocol == 1)
            {
                mGUIState["A Register"] = new JSON.Value(value);
                mGUIDirty = true;
            }
            return mA = value;
        }

        private Int16 wB(Int16 value)
        {
            if (vBPB)
            {
                Int32 p = value & 0x7fff;
                if (value < 0) p += 32768;
                lock (mBPB)
                {
                    if (mBPB[p])
                    {
                        Halt();
                        Console.Out.Write("[B:{0:x4}/{1}]", value, Program.Octal(value, 6));
                    }
                }
            }
            if (mGUIProtocol == 1)
            {
                mGUIState["B Register"] = new JSON.Value(value);
                mGUIDirty = true;
            }
            return mB = value;
        }

        private Int16 wIR(Int16 value)
        {
            if (vBPIR)
            {
                Int32 p = value & 0x7fff;
                if (value < 0) p += 32768;
                lock (mBPIR)
                {
                    if (mBPIR[p])
                    {
                        Halt();
                        Console.Out.Write("[IR:{0:x4}/{1}]", value, Program.Octal(value, 6));
                    }
                }
            }
            if (mGUIProtocol == 1)
            {
                mGUIState["Instruction"] = new JSON.Value(value);
                mGUIDirty = true;
            }
            return mIR = value;
        }

        private Int16 wPC(Int16 value)
        {
            if (vBPPC)
            {
                Int32 p = value & 0x7fff;
                lock (mBPPC)
                {
                    if (mBPPC[p])
                    {
                        Halt();
                        Console.Out.Write("[PC:{0:x4}/{1}]", value, Program.Octal(value, 5));
                    }
                }
            }
            if (mGUIProtocol == 1)
            {
                mGUIState["Program Counter"] = new JSON.Value(value);
                mGUIDirty = true;
            }
            return mPC = value;
        }

        private Int16 Read(Int32 addr)
        {
            if (vBPR)
            {
                Int16 n = mBPR[addr];
                if (n != 0)
                {
                    lock (mBPR)
                    {
                        n = mBPR[addr];
                        if (n > 0) mBPR[addr]--;
                    }
                    if ((n == 1) || (n == -1))
                    {
                        Halt();
                        Console.Out.Write("[PC:{0} IR:{1} {2}]", Program.Octal(mPC, 5), Program.Octal(mIR, 6), Program.Decode(mPC, mIR));
                    }
                }
            }
            return mCore[addr];
        }

        private Int16 Write(Int32 addr, Int16 value)
        {
            if (vBPW)
            {
                Int16 n = mBPW[addr];
                if (n != 0)
                {
                    lock (mBPW)
                    {
                        n = mBPW[addr];
                        if (n > 0) mBPW[addr]--;
                    }
                    if ((n == 1) || (n == -1))
                    {
                        Halt();
                        Console.Out.Write("[PC:{0} IR:{1} {2}]", Program.Octal(mPC, 5), Program.Octal(mIR, 6), Program.Decode(mPC, mIR));
                    }
                }
            }
            return mCore[addr] = value;
        }

        private Boolean IO_Test(Int32 unit, Int16 command)
        {
            IO device = mIO[unit];
            if (device == null) return false;
            return device.Test(command);
        }

        private Boolean IO_Command(Int32 unit, Int16 command, Boolean wait)
        {
            IO device = mIO[unit];
            if (device == null) return false; // TODO: what if wait=true?
            Boolean ready = device.CommandReady;
            if ((!wait) && (!ready)) return false;
            DateTime start = DateTime.Now;
            while (!ready)
            {
                Thread.Sleep(10);
                ready = device.CommandReady;
                if ((DateTime.Now - start) > sIndicatorLag) break;
            }
            if (!ready)
            {
                SetIOHold();
                do Thread.Sleep(50); while (vIOHold && !device.CommandReady);
                ReleaseIOHold();
            }
            return device.Command(command);
        }

        private Boolean IO_Read(Int32 unit, out Int16 word, Boolean wait)
        {
            word = 0;
            IO device = mIO[unit];
            if (device == null) return false; // TODO: what if wait=true?
            Boolean ready = device.ReadReady;
            if ((!wait) && (!ready)) return false;
            DateTime start = DateTime.Now;
            while (!ready)
            {
                Thread.Sleep(10);
                ready = device.ReadReady;
                if ((DateTime.Now - start) > sIndicatorLag) break;
            }
            if (!ready)
            {
                SetIOHold();
                do Thread.Sleep(20); while (vIOHold && !device.ReadReady);
                ReleaseIOHold();
            }
            return device.Read(out word);
        }

        private Boolean IO_Write(Int32 unit, Int16 word, Boolean wait)
        {
            IO device = mIO[unit];
            if (device == null) return false; // TODO: what if wait=true?
            Boolean ready = device.WriteReady;
            if ((!wait) && (!ready)) return false;
            DateTime start = DateTime.Now;
            while (!ready)
            {
                Thread.Sleep(10);
                ready = device.WriteReady;
                if ((DateTime.Now - start) > sIndicatorLag) break;
            }
            if (!ready)
            {
                SetIOHold();
                do Thread.Sleep(20); while (vIOHold && !device.WriteReady);
                ReleaseIOHold();
            }
            return device.Write(word);
        }
    }
}

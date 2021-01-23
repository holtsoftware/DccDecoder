using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DccDecoder
{
	public class Command
	{
		public long FirstTick { get; set; }
		public long LastHigh { get; set; }
		public long CurBit { get; set; }
		public long State { get; set; }
		public long Preamble { get; set; }
		public long DccOne { get; set; }
		public long DccZero { get; set; }
		public long DccBad { get; set; }
		public byte[] Bits { get; } = new byte[64];
	}
}

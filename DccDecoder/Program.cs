using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Threading.Tasks;
using System.IO;
using Unosquare.PiGpio.NativeMethods;
using Unosquare.PiGpio.NativeEnums;

namespace DccDecoder
{
	class Program
	{
		const int TIMING_DATA_SIZE = 512;
		const int MAX_BITS = 64;

		/* Our Finite State Machine has two states. */
		const int STATE_PREAMBLE = 0;
		const int STATE_DATA = 1;
		/* Timing values found via trial and error */
		/* Remember, zeros s t r e t c h */
		const int TICKS_MIN_DCC_ONE = 12;
		const int TICKS_MAX_DCC_ONE = 38;
		const int TICKS_MIN_DCC_ZERO = 68;
		const int TICKS_MAX_DCC_ZERO = 150;

		/* Refresh time aka how often to print summary status, as set by option r */
		const int OPT_R_MIN = 1;
		const int OPT_R_MAX = 300;
		const int OPT_R_DEF = 10;

		/* Sample  time, as set by option s, more frequent sampling takes more CPU.  The RPi 3 is easily
		 * good for 1 though, so we make that the default. 
		 */
		const int OPT_S_MIN = 1;
		const int OPT_S_MAX = 10;
		const int OPT_S_DEF = 1;

		static readonly string[] two_eight_speeds = new string[]{
	"STOP",
	"E-STOP",
	"Step 1",
	"Step 3",
	"Step 5",
	"Step 7",
	"Step 9",
	"Step 11",
	"Step 13",
	"Step 15",
	"Step 17",
	"Step 19",
	"Step 21",
	"Step 23",
	"Step 25",
	"Step 27",
	"STOP",
	"E-STOP",
	"Step 2",
	"Step 4",
	"Step 6",
	"Step 8",
	"Step 10",
	"Step 12",
	"Step 14",
	"Step 16",
	"Step 18",
	"Step 20",
	"Step 22",
	"Step 24",
	"Step 26",
	"Step 28"
};

		static void ResetCommand(Command command)
		{
			/* Reset everything */
			command.State = STATE_PREAMBLE;
			command.Preamble = 0;
			command.CurBit = 0;
			Array.Clear(command.Bits, 0, command.Bits.Length);
		}

		static async void printf(string text, params object[] args)
		{
			await Task.Run(() => Console.Write(text, args));
		}

		static async void PrintMessage(bool[] data)
		{
			await Task.Run(() =>
			{ 
				foreach (var d in data)
				{
					Console.Write(d ? 1 : 0);
				}
				Console.WriteLine();
			});
		}

		static void PrintResults(List<long> high, List<long> low)
		{
			low.RemoveAll(i => i > 9385);
			Console.WriteLine($"High: Min({high.Min()}) Max({high.Max()}) Avg({high.Average()}) Count({high.Count}) SmallCount({high.Count(i => i < 100)})");
			//Console.WriteLine($"Low: Min({low.Min()}) Max({low.Max()}) Avg({low.Average()}) Count({low.Count})");
			using var stream = new StreamWriter("test.csv");
			foreach(var item in high)
			{
				stream.WriteLine(item);
			}
			Environment.Exit(0);
		}

		static uint lastHigh = 0;

		static void Change(UserGpio pin, LevelChange change, uint time)
		{
			if(change == LevelChange.LowToHigh)
			{
				lastHigh = time;
			}
			else if(change == LevelChange.HighToLow)
			{
				uint diff = time - lastHigh;
				if(diff >= TICKS_MIN_DCC_ONE && diff <= TICKS_MAX_DCC_ONE)
				{
					AddDidget(true);
				}
				else if(diff >= TICKS_MIN_DCC_ZERO && diff <= TICKS_MAX_DCC_ZERO)
				{
					AddDidget(false);
				}
			}
		}

		static ConcurrentQueue<bool> theData = new ConcurrentQueue<bool>(); 

		static async void AddDidget(bool b)
		{
			await Task.Run(() => theData.Enqueue(b));
		}

		static async Task Main2(string[] args)
		{
			Setup.GpioInitialise();

			uint lastHigh = 0;

			IO.GpioSetAlertFunc(Unosquare.PiGpio.NativeEnums.UserGpio.Bcm04, Change);

			IO.GpioSetMode(Unosquare.PiGpio.NativeEnums.SystemGpio.Bcm04, Unosquare.PiGpio.NativeEnums.PinMode.Input);


			while(true)
			{
				await Task.Delay(1000);
				while(theData.TryDequeue(out var r))
				{
					Console.Write(r ? 1 : 0);
				}
				Console.WriteLine();
			}

		}

		static async Task Main(string[] args)
		{
			var queue = new ConcurrentQueue<bool>();
			var lastHigh = DateTime.Now.Ticks;
			using var gpioController = new GpioController();
			var high = new List<long>();
			var low = new List<long>();

			var lastPin = PinEventTypes.None;

			gpioController.OpenPin(4, System.Device.Gpio.PinMode.InputPullUp);
			gpioController.RegisterCallbackForPinValueChangedEvent(4, PinEventTypes.Falling | PinEventTypes.Rising, (sender, args) =>
			{
				if(lastPin == args.ChangeType)
				{
					Console.WriteLine($"Duplicate Pin {lastPin}");
					return;
				}
				lastPin = args.ChangeType;
				if (args.ChangeType == PinEventTypes.Rising)
				{
					lastHigh = DateTime.Now.Ticks;
				}
				else if(args.ChangeType == PinEventTypes.Falling)
				{
					var ticks = DateTime.Now.Ticks;
					var differenc = (ticks - lastHigh);
					//high.Add(differenc);

					if(differenc > 200 && differenc < 500)
					{
						//high.Add(differenc);
						//Console.Write("1");
						queue.Enqueue(true);
					}
					else if(differenc >= 500 && differenc < 900)
					{
						//low.Add(differenc);
						//Console.Write("0");
						queue.Enqueue(false);
					}

					/*if(high.Count > 1000000)
					{
						gpioController.ClosePin(4);
						PrintResults(high, low);
					}*/
				}

				return;

			});

			var preambleSent = false;
			var oneCount = 0;
			var data = new List<byte>();
			while (true)
			{
				if (queue.TryDequeue(out var bit))
				{
					if (!preambleSent)
					{
						if (bit)
						{
							oneCount++;
						}
						else
						{
							if (oneCount >= 10)
							{
								preambleSent = true;
						//		Console.WriteLine("PreambleSent {0}", oneCount);
							}
						}
					}
					else
					{
						var package = await ReadPackageAsync(queue);
						if (package.Count > 1)
						{
							PrintPackage(package);
						}
						oneCount = 0;
						preambleSent = false;
					}
				}
				else
				{
					await Task.Delay(250);
				}
			}

			static void PrintPackage(List<byte> package)
			{
				foreach(var d in package)
				{
					Console.Write(d.ToString("X2"));
					Console.Write(" ");
				}
				Console.WriteLine();
			}

			static async void ProcessData(List<byte> data)
			{
				await Task.Run(() =>
				{
					foreach(var d in data)
					{
						Console.Write(d.ToString("X4"));
						Console.Write(" ");
					}
					Console.WriteLine();
				});
			}

			static async Task<List<byte>> ReadPackageAsync(ConcurrentQueue<bool> queue)
			{
				var errorCheckSent = false;
				var readBites = 0;
				short b = 0;
				var packet = new List<byte>();

				while(!errorCheckSent)
				{
					readBites = 0;
					b = 0;
					while(readBites < 9)
					{
						if(queue.TryDequeue(out var bit))
						{
							readBites++;
							b = (short)((b << 1) | (bit ? 1 : 0));
						}
						else
						{
							await Task.Delay(10);
						}
					}

					if((b&1) == 0)
					{
						packet.Add((byte)(b >> 1));
					}
					else if((b & 1) == 1)
					{
						packet.Add((byte)(b >> 1));
						errorCheckSent = true;
					}
				}

				return packet;
			}

			static async Task<(byte? data,bool wasErrorCheck)> ReadByteAsync(ConcurrentQueue<bool> queue)
			{
				var readBites = 0;
				short b = 0;

				while(readBites < 9)
				{
					if(queue.TryDequeue(out var bit))
					{
						readBites++;
						b = (short)((b << 1) | ((bit) ? 1 : 0));
					}
					else
					{
						await Task.Delay(10);
					}
				}

				if((b & 1) == 0)
				{
					// valid byte
					return ((byte)(b >> 1), false);
				}
				else if((b >> 1) == 67)
				{
					// Error Check byte was sent
					return (null, true);
				}
				else
				{
					// invalid byte
					return (null, false);
				}

			}

		}
	}
}

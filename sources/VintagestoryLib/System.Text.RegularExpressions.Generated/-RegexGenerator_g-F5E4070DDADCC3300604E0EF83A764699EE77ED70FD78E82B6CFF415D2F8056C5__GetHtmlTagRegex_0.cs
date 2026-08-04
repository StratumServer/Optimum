using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;

namespace System.Text.RegularExpressions.Generated;

[GeneratedCode("System.Text.RegularExpressions.Generator", "10.0.13.11305")]
[SkipLocalsInit]
internal sealed class _003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__GetHtmlTagRegex_0 : Regex
{
	private sealed class RunnerFactory : RegexRunnerFactory
	{
		private sealed class Runner : RegexRunner
		{
			protected override void Scan(ReadOnlySpan<char> inputSpan)
			{
				while (TryFindNextPossibleStartingPosition(inputSpan) && !TryMatchAtCurrentPosition(inputSpan) && runtextpos != inputSpan.Length)
				{
					runtextpos++;
					if (_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_hasTimeout)
					{
						CheckTimeout();
					}
				}
			}

			private bool TryFindNextPossibleStartingPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				if (num <= inputSpan.Length - 3)
				{
					int num2 = inputSpan.Slice(num).IndexOf('<');
					if (num2 >= 0)
					{
						runtextpos = num + num2;
						return true;
					}
				}
				runtextpos = inputSpan.Length;
				return false;
			}

			private bool TryMatchAtCurrentPosition(ReadOnlySpan<char> inputSpan)
			{
				int num = runtextpos;
				int start = num;
				int num2 = 0;
				int num3 = 0;
				int num4 = 0;
				int num5 = 0;
				int arg = 0;
				int arg2 = 0;
				int num6 = 0;
				int pos = 0;
				ReadOnlySpan<char> readOnlySpan = inputSpan.Slice(num);
				if (readOnlySpan.IsEmpty || readOnlySpan[0] != '<')
				{
					UncaptureUntil(0);
					return false;
				}
				if ((uint)readOnlySpan.Length > 1u && readOnlySpan[1] == '/')
				{
					readOnlySpan = readOnlySpan.Slice(1);
					num++;
				}
				num++;
				readOnlySpan = inputSpan.Slice(num);
				num2 = num;
				num4 = num;
				int i;
				for (i = 0; (uint)i < (uint)readOnlySpan.Length; i++)
				{
					char c;
					if ((((c = readOnlySpan[i]) < '\u0080') ? ("\0\0\u2000Ͽ\ufffe蟿\ufffe߿"[(int)c >> 4] & (1 << (c & 0xF))) : (RegexRunner.CharInClass(c, "\0\u0002\n-.\0\u0002\u0004\u0005\u0003\u0001\u0006\t\u0013\0") ? 1 : 0)) == 0)
					{
						break;
					}
				}
				if (i == 0)
				{
					UncaptureUntil(0);
					return false;
				}
				readOnlySpan = readOnlySpan.Slice(i);
				num += i;
				num5 = num;
				num4++;
				while (true)
				{
					num3 = Crawlpos();
					Capture(1, num2, num);
					num6 = 0;
					while (true)
					{
						_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.StackPush(ref runstack, ref pos, Crawlpos(), num);
						num6++;
						if (!readOnlySpan.IsEmpty && readOnlySpan[0] == ' ')
						{
							num++;
							readOnlySpan = inputSpan.Slice(num);
							arg2 = num;
							goto IL_01d7;
						}
						goto IL_020e;
						IL_01d7:
						arg = Crawlpos();
						_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.StackPush(ref runstack, ref pos, arg2, arg);
						if (num6 == 0)
						{
							continue;
						}
						goto IL_0260;
						IL_0260:
						if (!readOnlySpan.IsEmpty && readOnlySpan[0] == '/')
						{
							readOnlySpan = readOnlySpan.Slice(1);
							num++;
						}
						if (readOnlySpan.IsEmpty || readOnlySpan[0] != '>')
						{
							if (_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_hasTimeout)
							{
								CheckTimeout();
							}
							if (num6 != 0)
							{
								_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.StackPop(runstack, ref pos, out arg, out arg2);
								UncaptureUntil(arg);
								if (_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_hasTimeout)
								{
									CheckTimeout();
								}
								num = arg2;
								readOnlySpan = inputSpan.Slice(num);
								if (!readOnlySpan.IsEmpty && readOnlySpan[0] != '\n')
								{
									num++;
									readOnlySpan = inputSpan.Slice(num);
									arg2 = num;
									goto IL_01d7;
								}
								goto IL_020e;
							}
							break;
						}
						Capture(0, start, runtextpos = num + 1);
						return true;
						IL_020e:
						if (--num6 < 0)
						{
							break;
						}
						num = runstack[--pos];
						UncaptureUntil(runstack[--pos]);
						readOnlySpan = inputSpan.Slice(num);
						goto IL_0260;
					}
					UncaptureUntil(num3);
					if (_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_hasTimeout)
					{
						CheckTimeout();
					}
					if (num4 >= num5)
					{
						break;
					}
					num = --num5;
					readOnlySpan = inputSpan.Slice(num);
				}
				UncaptureUntil(0);
				return false;
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				void UncaptureUntil(int capturePosition)
				{
					while (Crawlpos() > capturePosition)
					{
						Uncapture();
					}
				}
			}
		}

		protected override RegexRunner CreateInstance()
		{
			return new Runner();
		}
	}

	internal static readonly _003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__GetHtmlTagRegex_0 Instance = new _003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__GetHtmlTagRegex_0();

	private _003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__GetHtmlTagRegex_0()
	{
		pattern = "</?([\\w\\-]+)(?: .*?)?/?>";
		roptions = RegexOptions.None;
		Regex.ValidateMatchTimeout(_003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_defaultTimeout);
		internalMatchTimeout = _003CRegexGenerator_g_003EF5E4070DDADCC3300604E0EF83A764699EE77ED70FD78E82B6CFF415D2F8056C5__Utilities.s_defaultTimeout;
		factory = new RunnerFactory();
		capsize = 2;
	}
}

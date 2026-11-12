using System.Text;

using Pulse.Core.Interop;

namespace Pulse.Core.Tests.Interop;

/// <summary>The parsers of the Linux kernel's text files, on samples of what the kernel writes.</summary>
public sealed class LinuxProcTests
{
	[Fact]
	public void TryParseStat_NameWithSpacesAndParentheses_ReadsTheFieldsAfterIt()
	{
		// Arrange
		var text = "4242 (Web Content (x)) S 1200 4242 1200 0 -1 4194560 100 0 0 0 750 125 0 0 20 0 31 0 98765 1000 200"u8;

		// Act
		var parsed = LinuxProc.TryParseStat(text, out var stat, out var name);

		// Assert
		Assert.True(parsed);
		Assert.Equal("Web Content (x)", Encoding.UTF8.GetString(name));
		Assert.Equal(new ProcStat(1200, 750, 125, 31, 98765), stat);
	}

	[Fact]
	public void TryParseStat_CutOff_IsFalse()
		=> Assert.False(LinuxProc.TryParseStat("12 (bash) S 1 12"u8, out _, out _));

	[Fact]
	public void PrivatePages_IsResidentMinusShared()
		=> Assert.Equal(700, LinuxProc.PrivatePages("5000 1000 300 10 0 900 0\n"u8));

	[Fact]
	public void IoBytes_AddsReadAndWrittenCharacters()
		=> Assert.Equal(1500, LinuxProc.IoBytes("rchar: 1000\nwchar: 500\nsyscr: 7\nread_bytes: 4096\n"u8));

	[Theory]
	[InlineData("/usr/lib/chromium/chrome_crashpad_handler\0--monitor\0", "chrome_crashpad_handler")]
	[InlineData("postgres: checkpointer process", "postgres:")]
	[InlineData("", null)]
	public void ProgramName_IsTheFileNameOfTheFirstArgument(string commandLine, string? expected)
		=> Assert.Equal(expected, LinuxProc.ProgramName(Encoding.UTF8.GetBytes(commandLine)));

	[Fact]
	public void ParseCpu_ReadsTotalCoresAndBootTime()
	{
		// Arrange
		const string Text = """
			cpu  100 0 50 800 50 0 0 0 0 0
			cpu0 60 0 20 400 20 0 0 0 0 0
			cpu1 40 0 30 400 30 0 0 0 0 0
			intr 12345
			btime 1762900000
			""";
		var cores = new List<CpuTimes>();

		// Act
		var total = LinuxProc.ParseCpu(Text, cores, out var boot);

		// Assert: busy is everything but idle and iowait.
		Assert.Equal(new CpuTimes(150, 1000), total);
		Assert.Equal([new CpuTimes(80, 500), new CpuTimes(70, 500)], cores);
		Assert.Equal(1762900000, boot);
	}

	[Fact]
	public void MemInfo_ConvertsKilobytes()
	{
		const string Text = "MemTotal:       32768 kB\nMemFree:  1024 kB\nMemAvailable:   16384 kB\n";
		Assert.Equal(32768 * 1024.0, LinuxProc.MemInfo(Text, "MemTotal"));
		Assert.Equal(16384 * 1024.0, LinuxProc.MemInfo(Text, "MemAvailable"));
		Assert.True(double.IsNaN(LinuxProc.MemInfo(Text, "SwapTotal")));
	}

	[Fact]
	public void ParseDiskStats_KeepsTheIncludedDevices()
	{
		// Arrange
		const string Text = """
			 259       0 nvme0n1 1000 10 20000 300 2000 20 40000 600 0 900 1200 0 0 0 0
			 259       1 nvme0n1p1 10 0 200 3 20 0 400 6 0 9 12 0 0 0 0
			   7       0 loop0 5 0 10 1 0 0 0 0 0 1 1 0 0 0 0
			""";

		// Act
		var disks = LinuxProc.ParseDiskStats(Text, name => name == "nvme0n1");

		// Assert
		Assert.Equal([new DiskCounters("nvme0n1", 20000, 40000, 900)], disks);
	}

	[Fact]
	public void ParseNetDev_ReadsReceivedAndSentBytes()
	{
		// Arrange
		const string Text = """
			Inter-|   Receive                                                |  Transmit
			 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
			    lo:  5000      50    0    0    0     0          0         0     5000      50    0    0    0     0       0          0
			enp5s0: 123456789 100000  0    0    0     0          0       100 98765432   80000    0    0    0     0       0          0
			""";

		// Act
		var interfaces = LinuxProc.ParseNetDev(Text);

		// Assert
		Assert.Equal([("lo", 5000UL, 5000UL), ("enp5s0", 123456789UL, 98765432UL)], interfaces);
	}

	[Theory]
	[InlineData("NAME=\"Ubuntu\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n", "Ubuntu 24.04.1 LTS")]
	[InlineData("NAME=Fedora\nVERSION='42'\n", "Fedora 42")]
	[InlineData("ID=custom\n", null)]
	public void OsReleaseName_PrefersThePrettyName(string text, string? expected)
		=> Assert.Equal(expected, LinuxProc.OsReleaseName(text));

	[Theory]
	[InlineData("processor\t: 0\nmodel name\t: AMD Ryzen 7 7800X3D 8-Core Processor\n", "AMD Ryzen 7 7800X3D 8-Core Processor")]
	[InlineData("processor\t: 0\nBogoMIPS\t: 108.00\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n", "Raspberry Pi 5 Model B Rev 1.0")]
	public void CpuName_ReadsTheModel(string text, string expected)
		=> Assert.Equal(expected, LinuxProc.CpuName(text));

	[Fact]
	public void ParseMounts_KeepsBlockDevices_AndUnescapesSpaces()
	{
		// Arrange
		const string Text = """
			proc /proc proc rw,nosuid 0 0
			/dev/nvme0n1p2 / btrfs rw,subvol=/@ 0 0
			/dev/nvme0n1p2 /home btrfs rw,subvol=/@home 0 0
			/dev/loop3 /snap/core/1 squashfs ro 0 0
			/dev/sdb1 /run/media/me/My\040Disk exfat rw 0 0
			tmpfs /tmp tmpfs rw 0 0
			""";

		// Act
		var mounts = LinuxProc.ParseMounts(Text);

		// Assert
		Assert.Equal(
			[("/dev/nvme0n1p2", "/", "btrfs"), ("/dev/nvme0n1p2", "/home", "btrfs"), ("/dev/sdb1", "/run/media/me/My Disk", "exfat")],
			mounts);
	}

	[Fact]
	public void PciDeviceName_PrefersTheSubsystem_ThenTheMarketingName()
	{
		// Arrange
		string[] lines =
		[
			"# comment",
			"1002  Advanced Micro Devices, Inc. [AMD/ATI]",
			"\t7480  Navi 33 [Radeon RX 7600/7600 XT/7600M XT/7600S/7700S / PRO W7600]",
			"\t7590  Navi 44 [Radeon RX 9060 XT]",
			"\t\t1da2 e490  Pulse Radeon RX 9060 XT",
			"10de  NVIDIA Corporation",
			"\t2786  AD104 [GeForce RTX 4070]",
		];

		// Act & Assert
		Assert.Equal("Pulse Radeon RX 9060 XT", LinuxProc.PciDeviceName(lines, 0x1002, 0x7590, 0x1DA2, 0xE490));
		Assert.Equal("Radeon RX 9060 XT", LinuxProc.PciDeviceName(lines, 0x1002, 0x7590, 0x1002, 0x0001));
		Assert.Equal("GeForce RTX 4070", LinuxProc.PciDeviceName(lines, 0x10DE, 0x2786, 0, 0));
		Assert.Null(LinuxProc.PciDeviceName(lines, 0x8086, 0x1234, 0, 0));
	}

	[Theory]
	[InlineData("0x1002\n", 0x1002u)]
	[InlineData("10de", 0x10DEu)]
	[InlineData(null, 0u)]
	public void HexId_ReadsSysfsIds(string? text, uint expected) => Assert.Equal(expected, LinuxProc.HexId(text));

	[Fact]
	public void ParseTcpListeners_ListeningOnly_WithPortLoopbackAndInode()
	{
		// Arrange
		const string Text = """
			  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
			   0: 0100007F:1388 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41234 1 0000000000000000 100 0 0 10 0
			   1: 00000000:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 41235 1 0000000000000000 100 0 0 10 0
			   2: 0F02000A:C350 0100007F:1388 01 00000000:00000000 00:00000000 00000000  1000        0 41236 1 0000000000000000 20 4 30 10 -1
			""";

		// Act
		var listeners = LinuxProc.ParseTcpListeners(Text, ipv6: false);

		// Assert
		Assert.Equal([(5000, true, 41234L), (8080, false, 41235L)], listeners);
	}

	[Fact]
	public void ParseTcpListeners_Ipv6_LoopbackAndMappedLoopback()
	{
		// Arrange
		const string Text = """
			  sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
			   0: 00000000000000000000000001000000:0BB8 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 51001 1 0000000000000000 100 0 0 10 0
			   1: 0000000000000000FFFF00000100007F:0BB9 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 51002 1 0000000000000000 100 0 0 10 0
			   2: 00000000000000000000000000000000:0BBA 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 51003 1 0000000000000000 100 0 0 10 0
			""";

		// Act
		var listeners = LinuxProc.ParseTcpListeners(Text, ipv6: true);

		// Assert
		Assert.Equal([(3000, true, 51001L), (3001, true, 51002L), (3002, false, 51003L)], listeners);
	}
}

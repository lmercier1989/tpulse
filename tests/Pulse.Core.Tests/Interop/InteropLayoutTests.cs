using Pulse.Core.Interop;

namespace Pulse.Core.Tests.Interop;

/// <summary>
/// Pins the native structure layouts to the Windows SDK, and checks the network table against itself on the
/// running system: the interface type is stored twice (in the LUID and in its own field), so reading both at
/// the expected offsets proves the row layout.
/// </summary>
public sealed unsafe class InteropLayoutTests
{
	[Fact]
	public void MibIfRow2_MatchesNetioapiLayout()
	{
		// Arrange
		MibIfRow2 row = default;
		var start = (byte*) &row;

		// Act & Assert
		Assert.Equal(1352, sizeof(MibIfRow2));
		Assert.Equal(1128L, (byte*) &row.Type - start);
		Assert.Equal(1152L, &row.InterfaceAndOperStatusFlags - start);
		Assert.Equal(1156L, (byte*) &row.OperStatus - start);
		Assert.Equal(1208L, (byte*) &row.InOctets - start);
		Assert.Equal(1280L, (byte*) &row.OutOctets - start);
	}

	[Fact]
	public void FixedSizeStructures_MatchTheirNativeSizes()
	{
		// Act & Assert
		Assert.Equal(16, sizeof(PdhFmtCounterValue));
		Assert.Equal(24, sizeof(PdhFmtCounterValueItem));
		Assert.Equal(64, sizeof(MemoryStatusEx));
		Assert.Equal(64, sizeof(D3dkmtAdapterPerfData));
		Assert.Equal(12, sizeof(D3dkmtOpenAdapterFromLuid));
		Assert.Equal(68, sizeof(NvmlPciInfo));
	}

	[Fact]
	public void PointerSizedStructures_MatchTheirNativeSizes()
	{
		Assert.SkipUnless(IntPtr.Size == 8, "The expected sizes are for 64-bit processes.");

		// Act & Assert
		Assert.Equal(312, sizeof(DxgiAdapterDesc1));
		Assert.Equal(24, sizeof(D3dkmtQueryAdapterInfo));
	}

	[Fact]
	public void GetIfTable2_EveryRowStoresItsTypeInItsLuid()
	{
		// Arrange
		Assert.Equal(0u, Native.GetIfTable2(out var table));

		try
		{
			var count = *(uint*) table;
			var rows = (MibIfRow2*) ((byte*) table + 8);

			// Act & Assert: NET_LUID keeps the interface type in its top 16 bits.
			Assert.True(count > 0);
			for (var i = 0; i < count; i++)
			{
				Assert.Equal(rows[i].Type, (uint) (rows[i].InterfaceLuid >> 48));
				Assert.InRange(rows[i].OperStatus, 1u, 7u);
			}
		}
		finally
		{
			Native.FreeMibTable(table);
		}
	}

	[Fact]
	public void DxgiAdapters_ListsEveryAdapterOnce()
	{
		// Act
		var adapters = DxgiAdapters.Enumerate();

		// Assert
		Assert.Equal(adapters.Count, adapters.Select(adapter => adapter.Luid).Distinct().Count());
		Assert.All(adapters, adapter => Assert.False(string.IsNullOrWhiteSpace(adapter.Name)));
	}
}

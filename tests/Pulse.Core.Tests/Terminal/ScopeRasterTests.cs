using Pulse.Core.Processing;
using Pulse.Core.Terminal;

namespace Pulse.Core.Tests.Terminal;

public sealed class ScopeRasterTests
{
	private static readonly ScopePalette Palette = ScopePalette.Instance;

	[Fact]
	public void DrawLine_HorizontalTrace_IsSolidOnItsRowAndFaintBesideIt()
	{
		// Arrange
		var raster = new ScopeRaster(Palette);
		raster.Reset(10, 20);
		var ys = Enumerable.Repeat(10.5, 11).ToArray();
		var first = ScopePalette.Stroke(MetricKind.Cpu, 1);

		// Act
		raster.DrawLine(ys, 1.6, first, ScopePalette.StrokeLevels);

		// Assert
		Assert.Equal(ScopePalette.Stroke(MetricKind.Cpu, ScopePalette.StrokeLevels), Pixel(raster, 5, 10));
		Assert.Equal(ScopePalette.Stroke(MetricKind.Cpu, 2), Pixel(raster, 5, 9));
		Assert.Equal(ScopePalette.Stroke(MetricKind.Cpu, 2), Pixel(raster, 5, 11));
		Assert.Equal(ScopePalette.Background, Pixel(raster, 5, 7));
	}

	[Fact]
	public void DrawLine_Gap_IsLeftEmpty()
	{
		// Arrange
		var raster = new ScopeRaster(Palette);
		raster.Reset(10, 20);
		var ys = new double[11];
		ys.AsSpan().Fill(10.5);
		ys.AsSpan(4, 3).Fill(double.NaN);

		// Act
		raster.DrawLine(ys, 1.6, ScopePalette.Stroke(MetricKind.Cpu, 1), ScopePalette.StrokeLevels);

		// Assert: x = 4 and 5 touch no complete segment.
		Assert.Equal(ScopePalette.Background, Pixel(raster, 4, 10));
		Assert.Equal(ScopePalette.Background, Pixel(raster, 5, 10));
		Assert.NotEqual(ScopePalette.Background, Pixel(raster, 1, 10));
	}

	[Fact]
	public void DrawFill_ColoursOnlyBelowTheTrace()
	{
		// Arrange
		var raster = new ScopeRaster(Palette);
		raster.Reset(4, 40);
		var ys = Enumerable.Repeat(10.0, 5).ToArray();

		// Act
		raster.DrawFill(ys, top: 0, bottom: 39, ScopePalette.Fill(MetricKind.Memory, 1), ScopePalette.FillLevels, ScopePalette.FillAlpha);

		// Assert
		Assert.Equal(ScopePalette.Background, Pixel(raster, 1, 5));
		Assert.NotEqual(ScopePalette.Background, Pixel(raster, 1, 12));
		Assert.True(Palette.AlphaOf(Pixel(raster, 1, 12)) > Palette.AlphaOf(Pixel(raster, 1, 30)));
	}

	[Fact]
	public void DrawLine_OverFill_KeepsTheMoreOpaqueMark()
	{
		// Arrange
		var raster = new ScopeRaster(Palette);
		raster.Reset(4, 20);
		var ys = Enumerable.Repeat(5.5, 5).ToArray();
		raster.DrawLine(ys, 1.6, ScopePalette.Stroke(MetricKind.Cpu, 1), ScopePalette.StrokeLevels);

		// Act
		raster.DrawFill(ys, 0, 19, ScopePalette.Fill(MetricKind.Cpu, 1), ScopePalette.FillLevels, ScopePalette.FillAlpha);

		// Assert
		Assert.Equal(ScopePalette.Stroke(MetricKind.Cpu, ScopePalette.StrokeLevels), Pixel(raster, 2, 5));
	}

	[Theory]
	[InlineData(0.0, 0.20)]
	[InlineData(0.55, 0.07)]
	[InlineData(1.0, 0.0)]
	public void FillGradient_MatchesTheWindowGradient(double t, double expected)
	{
		// Act & Assert
		Assert.Equal(expected, ScopeRaster.FillGradient(t), 1e-9);
	}

	[Fact]
	public void DrawDot_IsSolidInTheMiddleAndClippedAtTheEdge()
	{
		// Arrange
		var raster = new ScopeRaster(Palette);
		raster.Reset(8, 8);

		// Act
		raster.DrawDot(7.5, 3.5, 2, ScopePalette.Stroke(MetricKind.Gpu, 1), ScopePalette.StrokeLevels);

		// Assert
		Assert.Equal(ScopePalette.Stroke(MetricKind.Gpu, ScopePalette.StrokeLevels), Pixel(raster, 7, 3));
		Assert.Equal(ScopePalette.Background, Pixel(raster, 0, 0));
	}

	private static byte Pixel(ScopeRaster raster, int x, int y) => raster.Pixels[(y * raster.Width) + x];
}

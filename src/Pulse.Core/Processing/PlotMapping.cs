namespace Pulse.Core.Processing;

/// <summary>
/// Maps sample time and value to plot pixels. Time runs left to right and <paramref name="RightTime"/> sits
/// on the right edge; values above the scale are flattened against the top like a clipping scope input.
/// </summary>
/// <param name="RightTime">The sample time shown at the right edge.</param>
/// <param name="Right">X of the right edge.</param>
/// <param name="PixelsPerSecond">Horizontal scale.</param>
/// <param name="Top">Y of the scale maximum.</param>
/// <param name="Bottom">Y of zero.</param>
/// <param name="PixelsPerUnit">Vertical scale: (bottom - top) / scale maximum.</param>
public readonly record struct PlotMapping(double RightTime, double Right, double PixelsPerSecond, double Top, double Bottom, double PixelsPerUnit)
{
	public double X(double time) => Right - ((RightTime - time) * PixelsPerSecond);

	public double Y(double value) => Math.Clamp(Bottom - (value * PixelsPerUnit), Top, Bottom);

	public double TimeAt(double x) => RightTime - ((Right - x) / PixelsPerSecond);
}

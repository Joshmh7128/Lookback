namespace Lookback.Calibration.Tests;

/// <summary>Made-up player frames: moving noise, a static HUD box, and a "feed" box that lights up at events.</summary>
internal sealed class Synthetic(int seed = 1)
{
    public const int W = 384, H = 216;
    private readonly Random _rng = new(seed);

    public (int X0, int Y0, int X1, int Y1) Hud { get; init; } = (16, 176, 96, 208);

    public (int X0, int Y0, int X1, int Y1) Feed { get; init; } = (280, 24, 368, 72);

    public byte[] Frame(bool feedLit)
    {
        byte[] px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                byte v = (byte)_rng.Next(30, 220);
                byte r = v, g = v, b = v;
                if (x >= Hud.X0 && x < Hud.X1 && y >= Hud.Y0 && y < Hud.Y1)
                {
                    r = g = b = 200;
                }
                else if (x >= Feed.X0 && x < Feed.X1 && y >= Feed.Y0 && y < Feed.Y1)
                {
                    (r, g, b) = feedLit ? ((byte)250, (byte)40, (byte)40) : ((byte)60, (byte)60, (byte)60);
                }

                px[i] = b;
                px[i + 1] = g;
                px[i + 2] = r;
                px[i + 3] = 255;
            }
        }

        return px;
    }
}

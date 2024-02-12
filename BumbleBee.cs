using System.Drawing;
using System.Diagnostics;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using System.ComponentModel.DataAnnotations;
using System.Xml.Linq;
using System.Runtime.ConstrainedExecution;

public class BumbleBee : ATAS.Strategies.Chart.ChartStrategy
{
    #region VARIABLES

    private List<int> lBars = new List<int>();

    private const int LONG = 1;
    private const int SHORT = 2;
    private const String sVersion = "Beta 1.2";
    private const int ACTIVE = 1;
    private const int STOPPED = 2;

    private int iMinADX = 0;
    private int iPrevOrderBar = -1;
    private int iFontSize = 12;
    private int iBotStatus = ACTIVE;
    private Stopwatch clock = new Stopwatch();
    private Rectangle rc = new Rectangle() { X = 50, Y = 50, Height = 200, Width = 400 };
    private DateTime dtStart = DateTime.Now;
    private String sLastTrade = String.Empty;
    private int _lastBar = -1;
    private decimal Volume = 1;
    private bool bAggressive = false;
    private bool bOvernight = false;

    #endregion

    #region INDICATORS

    private readonly AwesomeOscillator _ao = new AwesomeOscillator();
    private readonly ParabolicSAR _psar = new ParabolicSAR();
    private readonly EMA fastEma = new EMA() { Period = 20 };
    private readonly EMA slowEma = new EMA() { Period = 40 };
    private readonly EMA Ema200 = new EMA() { Period = 200 };
    private readonly FisherTransform _ft = new FisherTransform() { Period = 10 };
    private readonly SuperTrend _st = new SuperTrend() { Period = 10, Multiplier = 1m };
    private readonly KAMA _kama9 = new KAMA() { ShortPeriod = 2, LongPeriod = 109, EfficiencyRatioPeriod = 9 };
    private readonly T3 _t3 = new T3() { Period = 10, Multiplier = 1 };
    private readonly MACD _macd = new MACD() { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 };
    private readonly SqueezeMomentum _sq = new SqueezeMomentum() { BBPeriod = 20, BBMultFactor = 2, KCPeriod = 20, KCMultFactor = 1.5m, UseTrueRange = false };
    private readonly ADX _adx = new ADX() { Period = 10 };

    #endregion

    #region USER SETTINGS

    [Display(GroupName = "General", Name = "Aggressive Mode", Description = "Adds more contracts, faster.  But exits on first opposite colored candle")]
    public bool Aggressive { get => bAggressive; set { bAggressive = value; RecalculateValues(); } }
    [Display(GroupName = "General", Name = "Overnight Mode", Description = "The most paranoid settings, tries to ensure minimum risk")]
    public bool Overnight { get => bOvernight; set { bOvernight = value; RecalculateValues(); } }

    public int TextFont { get => iFontSize; set { iFontSize = value; RecalculateValues(); } }
    [Display(GroupName = "Filters", Name = "Minimum ADX", Description = "Minimum ADX value before buy/sell")]
    [Range(0, 100)]
    public int Min_ADX { get => iMinADX; set { if (value < 0) return; iMinADX = value; RecalculateValues(); } }

    #endregion

    #region RENDER CONTEXT

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var font = new RenderFont("Calibri", iFontSize);
        var fontB = new RenderFont("Calibri", iFontSize, FontStyle.Bold);
        int upY = 50;
        int upX = 50;
        var txt = String.Empty;

        switch (iBotStatus)
        {
            case ACTIVE:
                TimeSpan t = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds);
                String an = String.Format("{0:D2}:{1:D2}:{2:D2}", t.Hours, t.Minutes, t.Seconds);
                txt = $"BumbleBee ACTIVE on {TradingManager.Portfolio.AccountID} since " + dtStart.ToString() + " (" + an + ")";
                context.DrawString(txt, fontB, Color.Lime, upX, upY);
                if (!clock.IsRunning)
                    clock.Start();
                break;
            case STOPPED:
                txt = $"BumbleBee STOPPED on {TradingManager.Portfolio.AccountID}";
                context.DrawString(txt, fontB, Color.Orange, upX, upY);
                if (clock.IsRunning)
                    clock.Stop();
                break;
        }
        var tsize = context.MeasureString(txt, fontB);
        upY += tsize.Height + 6;

        if (TradingManager.Portfolio != null && TradingManager.Position != null)
        {
            txt = $"{TradingManager.MyTrades.Count()} trades, with PNL: {TradingManager.Position.RealizedPnL}";
            if (iBotStatus == STOPPED) { txt = String.Empty; sLastTrade = String.Empty; }
            context.DrawString(txt, font, Color.White, upX, upY);
            upY += tsize.Height + 6;
            txt = sLastTrade;
            context.DrawString(txt, font, Color.White, upX, upY);
        }
    }

    #endregion

    public BumbleBee()
    {
        EnableCustomDrawing = true;
        Add(_ao);
        Add(_ft);
        Add(_psar);
        Add(_st);
        Add(_kama9);
        Add(_sq);
        Add(_adx);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var pbar = bar - 1;
        var prevBar = _lastBar;
        _lastBar = bar;

        var candle = GetCandle(pbar);
        value = candle.Close;

        #region INDICATOR CALCULATIONS

        _t3.Calculate(pbar, value);
        fastEma.Calculate(pbar, value);
        slowEma.Calculate(pbar, value);
        _macd.Calculate(pbar, value);

        var ao = ((ValueDataSeries)_ao.DataSeries[0])[pbar];
        var kama9 = ((ValueDataSeries)_kama9.DataSeries[0])[pbar];
        var t3 = ((ValueDataSeries)_t3.DataSeries[0])[pbar];
        var e200 = ((ValueDataSeries)Ema200.DataSeries[0])[pbar];
        var fast = ((ValueDataSeries)fastEma.DataSeries[0])[pbar];
        var fastM = ((ValueDataSeries)fastEma.DataSeries[0])[pbar - 1];
        var slow = ((ValueDataSeries)slowEma.DataSeries[0])[pbar];
        var slowM = ((ValueDataSeries)slowEma.DataSeries[0])[pbar - 1];
        var f1 = ((ValueDataSeries)_ft.DataSeries[0])[pbar];
        var f2 = ((ValueDataSeries)_ft.DataSeries[1])[pbar];
        var psar = ((ValueDataSeries)_psar.DataSeries[0])[pbar];
        var m1 = ((ValueDataSeries)_macd.DataSeries[0])[pbar];
        var m2 = ((ValueDataSeries)_macd.DataSeries[1])[pbar];
        var m3 = ((ValueDataSeries)_macd.DataSeries[2])[pbar];
        var x = ((ValueDataSeries)_adx.DataSeries[0])[pbar];

        var sq2 = ((ValueDataSeries)_sq.DataSeries[1])[pbar];
        var sq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar];
        var psq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar - 1];
        var psq2 = ((ValueDataSeries)_sq.DataSeries[1])[pbar - 1];
        var ppsq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar - 2];
        var ppsq2 = ((ValueDataSeries)_sq.DataSeries[1])[pbar - 2];

        var t1 = ((fast - slow) - (fastM - slowM)) * 150;

        var fisherUp = (f1 < f2);
        var fisherDown = (f2 < f1);
        var psarBuy = (psar < candle.Close);
        var psarSell = (psar > candle.Close);
        var macdUp = (m1 > m2);
        var macdDown = (m1 < m2);

        #endregion

        #region CANDLE CALCULATIONS

        decimal _tick = ChartInfo.PriceChartContainer.Step;

        var red = candle.Close < candle.Open;
        var green = candle.Close > candle.Open;
        var p1C = GetCandle(pbar - 1);
        var p2C = GetCandle(pbar - 2);
        var p3C = GetCandle(pbar - 3);
        var c1G = p1C.Open < p1C.Close;
        var c1R = p1C.Open > p1C.Close;
        var c2G = p2C.Open < p2C.Close;
        var c2R = p2C.Open > p2C.Close;
        var c3G = p3C.Open < p3C.Close;
        var c3R = p3C.Open > p3C.Close;
        var c0Body = Math.Abs(candle.Close - candle.Open);

        var CrossUp9 = green && candle.Close > kama9;
        var CrossDown9 = red && candle.Close < kama9;

        var eqHigh = red && c1R && c2G && c3G && candle.Close < p1C.Close && (p1C.Open == p2C.Close || p1C.Open == p2C.Close + _tick || p1C.Open + _tick == p2C.Close);

        var eqLow = green && c1G && c2R && c3R && candle.Close > p1C.Close && (p1C.Open == p2C.Close || p1C.Open == p2C.Close + _tick || p1C.Open + _tick == p2C.Close);

        var upWickLarger = red && Math.Abs(candle.High - candle.Open) > Math.Abs(candle.Low - candle.Close);
        var downWickLarger = green && Math.Abs(candle.Low - candle.Open) > Math.Abs(candle.Close - candle.High);

        var Hammer = green && c0Body > Math.Abs(candle.High - candle.Close) && c0Body < Math.Abs(candle.Open - candle.Low);
        var revHammer = red && c0Body > Math.Abs(candle.Low - candle.Close) && c0Body < Math.Abs(candle.High - candle.Open);

        var under2 = candle.Close < e200;
        var over2 = candle.Close > e200;

        var bShowDown = true;
        var bShowUp = true;

        if (psarSell || !fisherUp || value < t3 || t1 < 0 || ao < 0)
            bShowUp = false;
        if (psarBuy || !fisherDown || value > t3 || t1 >= 0 || ao > 0)
            bShowDown = false;

        var TopSq = false; // (sq1 > 0 && sq1 < psq1 && psq1 > ppsq1);
        var BottomSq = false; // (sq1 < 0 && sq1 > psq1 && psq1 < ppsq1);

        bool BuyAdd = green && c1G && candle.Open > p1C.Close && CrossUp9 && CurrentPosition > 0;
        bool SellAdd = red && c1R && candle.Open < p1C.Close && CrossDown9 && CurrentPosition < 0;

        bool BuyMe = (macdUp || t1 > 0) && psarBuy && CrossUp9 && CurrentPosition == 0 && x > iMinADX;
        if (bAggressive)
            BuyMe = (macdUp || t1 > 0) && psarBuy;

        bool SellMe = (macdDown || t1 < 0) && psarSell && CrossDown9 && CurrentPosition == 0 && x > iMinADX;
        if (bAggressive)
            SellMe = (macdDown || t1 < 0) && psarSell;

        bool closeLong = (psarSell || t1 < 0 || BottomSq || CrossDown9) && CurrentPosition > 0;
        if (bAggressive || bOvernight)
            closeLong = (psarSell || t1 < 0 || BottomSq || CrossDown9 || red ) && CurrentPosition > 0;

        bool closeShort = (psarBuy || t1 > 0 || TopSq || CrossUp9) && CurrentPosition < 0;
        if (bAggressive || bOvernight)
            closeShort = (psarBuy || t1 > 0 || TopSq || CrossUp9 || green) && CurrentPosition < 0;

        #endregion

        if (closeLong)
            CloseCurrentPosition(GetReason(psarSell, t1 < 0, BottomSq, CrossDown9, revHammer));
        if (closeShort)
            CloseCurrentPosition(GetReason(psarBuy, t1 > 0, TopSq, CrossUp9, Hammer));

        if (BuyAdd)
            OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Buy);
        if (SellAdd)
            OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Sell);

        if (BuyMe)
            OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Buy);
        if (SellMe)
            OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Sell);

    }

    private void OpenPosition(String sReason, IndicatorCandle c, int bar, OrderDirections direction)
    {
        // Limit 1 order per bar
        if (iPrevOrderBar == bar)
            return;
        else
            iPrevOrderBar = bar;

        var sD = direction == OrderDirections.Buy ? sReason + " LONG (" + bar + ")" : sReason + " SHORT (" + bar + ")";
        sLastTrade = direction == OrderDirections.Buy ? "Bar " + bar + " - " + sReason + " LONG at " + c.Close : "Bar " + bar + " - " + sReason + " SHORT at " + c.Close;

        var order = new Order
        {
            Portfolio = Portfolio,
            Security = Security,
            Direction = direction,
            Type = OrderTypes.Market,
            QuantityToFill = 1, // GetOrderVolume(),
            Comment = sD
        };
        OpenOrder(order);
    }

    private void CloseCurrentPosition(String s)
    {
        var order = new Order
        {
            Portfolio = Portfolio,
            Security = Security,
            Direction = CurrentPosition > 0 ? OrderDirections.Sell : OrderDirections.Buy,
            Type = OrderTypes.Market,
            QuantityToFill = Math.Abs(CurrentPosition),
            Comment = "Position closed, reason: " + s
        };
        OpenOrder(order);
    }

    private decimal GetOrderVolume()
    {
        if (CurrentPosition == 0)
            return Volume;

        if (CurrentPosition > 0)
            return Volume + CurrentPosition;

        return Volume + Math.Abs(CurrentPosition);
    }

    private String GetReason(bool a, bool b, bool c, bool d, bool e)
    {
        var ham = CurrentPosition < 0 ? "Hammer candle" : "Reverse hammer candle";
        // psarSell || t1 < 0 || BottomSq || CrossDown9 || revHammer
        if (a) return "PSAR shifted";
        if (b) return "Waddah Explosion";
        if (c) return "Squeeze Relaxer";
        if (d) return "KAMA 9 cross";
        if (e) return ham;
        return "";
    }

}


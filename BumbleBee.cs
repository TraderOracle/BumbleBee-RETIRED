using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Diagnostics;
using System.Net;
using System.Windows.Input;
using System;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using ATAS.Strategies.Chart;
using ATAS.Indicators.Technical.Properties;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using static ATAS.Indicators.Technical.SampleProperties;

using System.Xml.Linq;
using System.Runtime.ConstrainedExecution;
using static System.Runtime.InteropServices.JavaScript.JSType;
using String = System.String;
using OFT.Rendering.Control;
using System.Runtime.InteropServices.JavaScript;

public class BumbleBee : ATAS.Strategies.Chart.ChartStrategy
{
    #region VARIABLES

    private List<int> lBars = new List<int>();

    private const int LONG = 1;
    private const int SHORT = 2;
    private const String sVersion = "Beta 1.6";
    private const int ACTIVE = 1;
    private const int STOPPED = 2;

    private bool bBigArrowUp = false;
    private int iPrevOrderBar = -1;
    private int iFontSize = 12;
    private int iBotStatus = ACTIVE;
    private Stopwatch clock = new Stopwatch();
    private Rectangle rc = new Rectangle() { X = 50, Y = 50, Height = 200, Width = 400 };
    private DateTime dtStart = DateTime.Now;
    private String sLastTrade = String.Empty;
    private int _lastBar = -1;
    private decimal Volume = 1;
    private bool bExitHighLow = false;
    private bool bExitHammer = false;
    private bool bExitSqueeze = false;
    private bool bExitKama9 = false;

    #endregion

    #region INDICATORS

    private readonly EMA _short = new() { Period = 3 };
    private readonly EMA _long = new() { Period = 10 };
    private readonly EMA _signal = new() { Period = 16 };

    private readonly AwesomeOscillator _ao = new AwesomeOscillator();
    private readonly ParabolicSAR _psar = new ParabolicSAR();
    private readonly EMA fastEma = new EMA() { Period = 20 };
    private readonly EMA slowEma = new EMA() { Period = 40 };
    private readonly EMA Ema200 = new EMA() { Period = 200 };
    private readonly FisherTransform _ft = new FisherTransform() { Period = 10 };
    private readonly SuperTrend _st = new SuperTrend() { Period = 10, Multiplier = 1m };
    private readonly KAMA _kama9 = new KAMA() { ShortPeriod = 2, LongPeriod = 109, EfficiencyRatioPeriod = 9 };
    private readonly T3 _t3 = new T3() { Period = 10, Multiplier = 1 };
    private readonly MACD _macd = new MACD() { ShortPeriod = 3, LongPeriod = 10, SignalPeriod = 16 };
    private readonly SqueezeMomentum _sq = new SqueezeMomentum() { BBPeriod = 20, BBMultFactor = 2, KCPeriod = 20, KCMultFactor = 1.5m, UseTrueRange = false };
    private readonly ADX _adx = new ADX() { Period = 10 };

    #endregion

    #region USER SETTINGS

    [Display(GroupName = "Exit trade when:", Name = "KAMA 9 cross")]
    public bool ExitKama9 { get => bExitKama9; set { bExitKama9 = value; RecalculateValues(); } }
    [Display(GroupName = "Exit trade when:", Name = "Equal High/Low")]
    public bool ExitHighLow { get => bExitHighLow; set { bExitHighLow = value; RecalculateValues(); } }
    [Display(GroupName = "Exit trade when:", Name = "Hammer candle")]
    public bool ExitHammer { get => bExitHammer; set { bExitHammer = value; RecalculateValues(); } }
    [Display(GroupName = "Exit trade when:", Name = "Reverse squeeze relaxer")]
    public bool ExitSqueeze { get => bExitSqueeze; set { bExitSqueeze = value; RecalculateValues(); } }

    [Display(GroupName = "General", Name = "Aggressive Mode", Description = "Adds more contracts, faster.  But exits on first opposite colored candle")]

    public int TextFont { get => iFontSize; set { iFontSize = value; RecalculateValues(); } }
    [Display(GroupName = "Filters", Name = "Minimum ADX", Description = "Minimum ADX value before buy/sell")]

    #endregion

    #region RENDER CONTEXT

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var font = new RenderFont("Calibri", iFontSize);
        var fontB = new RenderFont("Calibri", iFontSize, FontStyle.Bold);
        int upY = 50;
        int upX = 50;
        var txt = String.Empty;
        Size tsize;

        switch (iBotStatus)
        {
            case ACTIVE:
                TimeSpan t = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds);
                String an = String.Format("{0:D2}:{1:D2}:{2:D2}", t.Hours, t.Minutes, t.Seconds);
                txt = $"BumbleBee version " + sVersion;
                context.DrawString(txt, font, Color.LightPink, upX, upY);
                tsize = context.MeasureString(txt, fontB);
                upY += tsize.Height + 6;
                txt = $"ACTIVE on {TradingManager.Portfolio.AccountID} since " + dtStart.ToString() + " (" + an + ")";
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
        tsize = context.MeasureString(txt, fontB);
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
        if (bar < CurrentBar - 3)
            return;

        var pbar = bar - 1;
        var prevBar = _lastBar;
        _lastBar = bar;

        if (prevBar == bar)
            return;

        var candle = GetCandle(pbar);
        var pcandle = GetCandle(bar);
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
        var ppsar = ((ValueDataSeries)_psar.DataSeries[0])[bar];
        var m1 = ((ValueDataSeries)_macd.DataSeries[0])[pbar];
        var m2 = ((ValueDataSeries)_macd.DataSeries[1])[pbar];
        var m3shit = ((ValueDataSeries)_macd.DataSeries[2])[pbar];
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
        var ppsarBuy = (ppsar < pcandle.Close);
        var ppsarSell = (ppsar > pcandle.Close);

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

        var CrossUp9 = green && candle.Close > kama9 && bExitKama9;
        var CrossDown9 = red && candle.Close < kama9 && bExitKama9;

        var eqHigh = bExitHighLow && red && c1R && c2G && c3G && candle.Close < p1C.Close && (p1C.Open == p2C.Close || p1C.Open == p2C.Close + _tick || p1C.Open + _tick == p2C.Close);

        var eqLow = bExitHighLow && green && c1G && c2R && c3R && candle.Close > p1C.Close && (p1C.Open == p2C.Close || p1C.Open == p2C.Close + _tick || p1C.Open + _tick == p2C.Close);

        var upWickLarger = red && Math.Abs(candle.High - candle.Open) > Math.Abs(candle.Low - candle.Close);
        var downWickLarger = green && Math.Abs(candle.Low - candle.Open) > Math.Abs(candle.Close - candle.High);

        var under2 = candle.Close < e200;
        var over2 = candle.Close > e200;

        var TopSq = bExitSqueeze && (sq1 > 0 && sq1 < psq1 && psq1 > ppsq1);
        var BottomSq = bExitSqueeze && (sq1 < 0 && sq1 > psq1 && psq1 < ppsq1);

        bool BuyAdd = green && c1G && candle.Open > p1C.Close && CrossUp9 && CurrentPosition > 0;
        bool SellAdd = red && c1R && candle.Open < p1C.Close && CrossDown9 && CurrentPosition < 0;

        var macd = _short.Calculate(pbar, value) - _long.Calculate(pbar, value);
        var signal = _signal.Calculate(pbar, macd);
        var m3 = macd - signal;

        var Hammer = bExitHammer && green && c0Body > Math.Abs(candle.High - candle.Close) && c0Body < Math.Abs(candle.Open - candle.Low);
        var revHammer = bExitHammer && red && c0Body > Math.Abs(candle.Low - candle.Close) && c0Body < Math.Abs(candle.High - candle.Open);

        //bool closeLong = (psarSell || t1 < 0 || BottomSq || CrossDown9) && CurrentPosition > 0;
        bool closeLong = (psarSell || BottomSq || CrossDown9) && CurrentPosition > 0;
        //bool closeShort = (psarBuy || t1 > 0 || TopSq || CrossUp9) && CurrentPosition < 0;
        bool closeShort = (psarBuy || TopSq || CrossUp9) && CurrentPosition < 0;

        bool wickLong = CurrentPosition > 0 && green && candle.Close > kama9 && candle.Low < kama9;
        bool wickShort = CurrentPosition < 0 && red && candle.Open < kama9 && candle.High > kama9;

        #endregion

        if (closeLong)
            CloseCurrentPosition(GetReason(psarSell, t1 < 0, BottomSq, CrossDown9, revHammer), bar);
        //CloseCurrentPosition(GetReason(psarSell, t1 < 0, BottomSq, CrossDown9, revHammer), bar);

        if (closeShort)
            CloseCurrentPosition(GetReason(psarBuy, t1 > 0, TopSq, CrossUp9, Hammer), bar);

//        if (wickLong && CurrentPosition > 0)
//            OpenPosition("Candle wick ADD", candle, bar, OrderDirections.Buy);
//        if (wickShort && CurrentPosition < 0)
//            OpenPosition("Candle wick ADD", candle, bar, OrderDirections.Buy);

        if (BuyAdd && CurrentPosition > 0)
            OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Buy);
        if (SellAdd && CurrentPosition < 0)
            OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Sell);

        if (psarBuy && (m3 > 0 || t1 > 0) && CurrentPosition == 0)
            OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Buy);
        if (psarSell && (m3 < 0 || t1 < 0) && CurrentPosition == 0)
            OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Sell);

        if (prevBar != bar && false)
        {
            if ((green && CurrentPosition < -1) || (red && CurrentPosition > 1))
            {
                CloseCurrentPosition("GTFO opposite bar", bar);
                return;
            }
            if (green && CurrentPosition > 0)
                OpenPosition("Gluttony", candle, bar, OrderDirections.Buy);
            if (red && CurrentPosition < 0)
                OpenPosition("Gluttony", candle, bar, OrderDirections.Sell);
        }

    }

    #region POSITION METHODS

    private void OpenPosition(String sReason, IndicatorCandle c, int bar, OrderDirections direction)
    {
        if (iBotStatus == STOPPED)
        {
            AddLog("Attempted to open position, but bot was stopped");
            return;
        }
        if (CurrentPosition >= 10)
        {
            AddLog("Attempted to open more than 2 contracts, trade canceled");
            return;
        }

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
        AddLog(sLastTrade);
    }

    private void CloseCurrentPosition(String s, int bar)
    {
        if (iBotStatus == STOPPED)
        {
            AddLog("Attempted to close position, but bot was stopped");
            return;
        }

        // Limit 1 order per bar
        if (iPrevOrderBar == bar)
            return;
        else
            iPrevOrderBar = bar;

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

    #endregion

    #region MISC METHODS

    private String GetReason(bool a, bool b, bool c, bool d, bool e)
    {
        var ham = CurrentPosition < 0 ? "Hammer candle" : "Reverse hammer candle";
        // psarSell || m3 < 0 || BottomSq || CrossDown9 || revHammer
        if (a) return "PSAR change";
        if (b) return "MACD change";
        if (c) return "Squeeze Relaxer";
        if (d) return "KAMA 9 cross";
        if (e) return ham;
        return "";
    }


    public override bool ProcessKeyDown(KeyEventArgs e)
    {
        if (iBotStatus == ACTIVE)
            iBotStatus = STOPPED;
        else
            iBotStatus = ACTIVE;
        return false;
    }

    private bool IsPointInsideRectangle(Rectangle rectangle, Point point)
    {
        return point.X >= rectangle.X && point.X <= rectangle.X + rectangle.Width && point.Y >= rectangle.Y && point.Y <= rectangle.Y + rectangle.Height;
    }

    public override bool ProcessMouseClick(RenderControlMouseEventArgs e)
    {
        if (e.Button == RenderControlMouseButtons.Left && IsPointInsideRectangle(rc, e.Location))
        {
            if (iBotStatus == ACTIVE)
                iBotStatus = STOPPED;
            else
                iBotStatus = ACTIVE;
            return true;
        }

        return false;
    }

    private void AddLog(String s)
    {
        
    }

    #endregion

}


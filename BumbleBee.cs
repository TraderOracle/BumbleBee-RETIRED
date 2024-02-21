using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Input;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using OFT.Rendering.Control;
using static ATAS.Indicators.Technical.SampleProperties;

using String = System.String;
using Utils.Common.Logging;

public class BumbleBee : ATAS.Strategies.Chart.ChartStrategy
{
    #region VARIABLES

    private int iJunk = 0;
    private List<int> lBars = new List<int>();
    private Order globalOrder;

    private const int LONG = 1;
    private const int SHORT = 2;
    private const String sVersion = "Beta 2.0";
    private const int ACTIVE = 1;
    private const int STOPPED = 2;
    private int _lastBar = -1;
    private bool _lastBarCounted;

    private bool bBigArrowUp = false;
    private bool bAggressive = false;
    private int iPrevOrderBar = -1;
    private int iFontSize = 12;
    private int iMaxContracts = 20;
    private int iMaxLoss = 5000;
    private int iMaxProfit = 10000;
    private int iBotStatus = ACTIVE;
    private Stopwatch clock = new Stopwatch();
    private Rectangle rc = new Rectangle() { X = 50, Y = 50, Height = 200, Width = 400 };
    private DateTime dtStart = DateTime.Now;
    private String sLastTrade = String.Empty;
    private String sLastLog = String.Empty;
    private decimal Volume = 1;
    private bool bExitHammer = false;
    private bool bExitSqueeze = false;
    private bool bExitKama9 = false;

    #endregion

    #region INDICATORS

    private readonly SMA _short = new() { Period = 3 };
    private readonly SMA _long = new() { Period = 10 };
    private readonly SMA _signal = new() { Period = 16 };

    private readonly AwesomeOscillator _ao = new AwesomeOscillator();
    private readonly ParabolicSAR _psar = new ParabolicSAR();
    private readonly EMA fastEma = new EMA() { Period = 20 };
    private readonly EMA slowEma = new EMA() { Period = 40 };
    private readonly EMA Ema200 = new EMA() { Period = 200 };
    private readonly FisherTransform _ft = new FisherTransform() { Period = 10 };
    private readonly SuperTrend _st = new SuperTrend() { Period = 10, Multiplier = 1m };
    private readonly KAMA _kama9 = new KAMA() { ShortPeriod = 2, LongPeriod = 109, EfficiencyRatioPeriod = 9 };
    private readonly T3 _t3 = new T3() { Period = 10, Multiplier = 1 };
    private readonly SqueezeMomentum _sq = new SqueezeMomentum() { BBPeriod = 20, BBMultFactor = 2, KCPeriod = 20, KCMultFactor = 1.5m, UseTrueRange = false };
    private readonly ADX _adx = new ADX() { Period = 10 };

    #endregion

    #region USER SETTINGS

    [Display(GroupName = "Exit trade when:", Name = "KAMA 9 cross")]
    public bool ExitKama9 { get => bExitKama9; set { bExitKama9 = value; RecalculateValues(); } }
    [Display(GroupName = "Exit trade when:", Name = "Hammer candle")]
    public bool ExitHammer { get => bExitHammer; set { bExitHammer = value; RecalculateValues(); } }
    [Display(GroupName = "Exit trade when:", Name = "Reverse squeeze relaxer")]
    public bool ExitSqueeze { get => bExitSqueeze; set { bExitSqueeze = value; RecalculateValues(); } }
    public int TextFont { get => iFontSize; set { iFontSize = value; RecalculateValues(); } }

    [Display(Name = "Max simultaneous contracts", GroupName = "General")]
    [Range(1, 90)]
    public int AdvMaxContracts { get => iMaxContracts; set { iMaxContracts = value; RecalculateValues(); } }

    [Display(GroupName = "General", Name = "Aggressive Mode", Description = "Adds more contracts, faster.  But exits on first opposite colored candle")]
    public bool Aggressive { get => bAggressive; set { bAggressive = value; RecalculateValues(); } }

    [Display(GroupName = "General", Name = "Maximum Loss", Description = "Maximum amount of money lost before the bot shuts off")]
    [Range(1, 90000)]
    public int MaxLoss { get => iMaxLoss; set { iMaxLoss = value; RecalculateValues(); } }

    [Display(GroupName = "General", Name = "Maximum Profit", Description = "Maximum profit before the bot shuts off")]
    [Range(1, 90000)]
    public int MaxProfit { get => iMaxProfit; set { iMaxProfit = value; RecalculateValues(); } }


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

        if (sLastLog != String.Empty && iBotStatus == ACTIVE)
        {
            upY += tsize.Height + 6;
            txt = $"Last Log: " + sLastLog;
            context.DrawString(txt, font, Color.Yellow, upX, upY);
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
        if (bar == 0)
        {
            _lastBarCounted = false;
            return;
        }
        else if (bar < CurrentBar - 3)
            return;

        if (ClosedPnL >= iMaxLoss)
        {
            AddLog("Max loss reached, bot is shutting off");
            iBotStatus = STOPPED;
        }
        if (ClosedPnL >= iMaxProfit)
        {
            AddLog("Max profit reached, bot is shutting off");
            iBotStatus = STOPPED;
        }

        var pbar = bar - 1;
        var prevBar = _lastBar;
        _lastBar = bar;

        if (prevBar == bar)
            return;

        var candle = GetCandle(pbar);
        value = candle.Close;

        #region INDICATOR CALCULATIONS

        fastEma.Calculate(pbar, value);
        slowEma.Calculate(pbar, value);

        var kama9 = ((ValueDataSeries)_kama9.DataSeries[0])[pbar];
        var e200 = ((ValueDataSeries)Ema200.DataSeries[0])[pbar];
        var fast = ((ValueDataSeries)fastEma.DataSeries[0])[pbar];
        var fastM = ((ValueDataSeries)fastEma.DataSeries[0])[pbar - 1];
        var slow = ((ValueDataSeries)slowEma.DataSeries[0])[pbar];
        var slowM = ((ValueDataSeries)slowEma.DataSeries[0])[pbar - 1];
        var psar = ((ValueDataSeries)_psar.DataSeries[0])[pbar];

        var sq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar];
        var psq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar - 1];
        var ppsq1 = ((ValueDataSeries)_sq.DataSeries[0])[pbar - 2];

        // Linda MACD
        var macd = _short.Calculate(pbar, value) - _long.Calculate(pbar, value);
        var signal = _signal.Calculate(pbar, macd);
        var m3 = macd - signal;

        var t1 = ((fast - slow) - (fastM - slowM)) * 150;

        var psarBuy = (psar < candle.Close);
        var psarSell = (psar > candle.Close);

        #endregion

        #region CANDLE CALCULATIONS

        decimal _tick = ChartInfo.PriceChartContainer.Step;

        var red = candle.Close < candle.Open;
        var green = candle.Close > candle.Open;
        var p1C = GetCandle(pbar - 1);
        var c1G = p1C.Open < p1C.Close;
        var c1R = p1C.Open > p1C.Close;

        var c0Body = Math.Abs(candle.Close - candle.Open);

        var CrossUp9 = green && candle.Close > kama9 && bExitKama9;
        var CrossDown9 = red && candle.Close < kama9 && bExitKama9;

        var TopSq = bExitSqueeze && (sq1 > 0 && sq1 < psq1 && psq1 > ppsq1);
        var BottomSq = bExitSqueeze && (sq1 < 0 && sq1 > psq1 && psq1 < ppsq1);

        bool BuyAdd = green && c1G && candle.Open > p1C.Close && CurrentPosition > 0;
        bool SellAdd = red && c1R && candle.Open < p1C.Close && CurrentPosition < 0;

        var Hammer = bExitHammer && green && c0Body > Math.Abs(candle.High - candle.Close) && c0Body < Math.Abs(candle.Open - candle.Low);
        var revHammer = bExitHammer && red && c0Body > Math.Abs(candle.Low - candle.Close) && c0Body < Math.Abs(candle.High - candle.Open);

        bool closeLong = (psarSell || t1 < 0 || BottomSq || CrossDown9) && CurrentPosition > 0;
        bool closeShort = (psarBuy || t1 > 0 || TopSq || CrossUp9) && CurrentPosition < 0;

        bool wickLong = green && candle.Open > kama9 && candle.Low < kama9;
        bool wickShort = red && candle.Close < kama9 && candle.High > kama9;

        #endregion

        #region OPEN AND CLOSE POSITIONS

        if (true) // (_lastBar != bar)
        {
            if (true) // (_lastBarCounted)
            {
                if (bAggressive)
                {
                    if ((green && CurrentPosition < -1) || (red && CurrentPosition > 1))
                    {
                        CloseCurrentPosition("Opposite bar exit", bar);
                        return;
                    }
                    if (green && CurrentPosition > 0)
                        OpenPosition("Aggressive ADD", candle, bar, OrderDirections.Buy);
                    if (red && CurrentPosition < 0)
                        OpenPosition("Aggressive ADD", candle, bar, OrderDirections.Sell);
                }

                if (closeLong)
                    CloseCurrentPosition(GetReason(psarSell, false, BottomSq, CrossDown9, revHammer), bar);
                if (closeShort)
                    CloseCurrentPosition(GetReason(psarBuy, false, TopSq, CrossUp9, Hammer), bar);

                if (wickLong && CurrentPosition > 0)
                    OpenPosition("Candle wick ADD", candle, bar, OrderDirections.Buy);
                if (wickShort && CurrentPosition < 0)
                    OpenPosition("Candle wick ADD", candle, bar, OrderDirections.Sell);

                if (BuyAdd && CurrentPosition > 0)
                    OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Buy);
                if (SellAdd && CurrentPosition < 0)
                    OpenPosition("Volume Imbalance ADD", candle, bar, OrderDirections.Sell);

                if (psarBuy && (m3 > 0 || t1 > 0) && CurrentPosition == 0)
                    OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Buy);
                if (psarSell && (m3 < 0 || t1 < 0) && CurrentPosition == 0)
                    OpenPosition("MACD / PSAR", candle, bar, OrderDirections.Sell);
            }
            _lastBar = bar;
        }
        else
        {
            if (!_lastBarCounted)
                _lastBarCounted = true;
        }

        #endregion

    }

    #region POSITION METHODS

    private void ShittyWaitMethod()
    {
        switch (globalOrder.Status())
        {
            case OrderStatus.Canceled:
                break;
            case OrderStatus.Filled:
                break;
            default:
                Thread.Sleep(500);
                ShittyWaitMethod();
                break;
        }
    }

    private void BouncePosition(String sReason, IndicatorCandle c, int bar, OrderDirections direction)
    {
        if (CurrentPosition != 0)
        {
            CloseCurrentPosition("Closing current before opening new", bar);
            OpenPosition(sReason, c, bar, direction);
        }
        else
        {
            OpenPosition(sReason, c, bar, direction);
        }
    }

    private void OpenPosition(String sReason, IndicatorCandle c, int bar, OrderDirections direction)
    {
        if (iBotStatus == STOPPED)
        {
            AddLog("Attempted to open position, but bot was stopped");
            return;
        }
        if (CurrentPosition >= iMaxContracts)
        {
            AddLog("Attempted to open more than (max) contracts, trade canceled");
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
        globalOrder = order;
        OpenOrder(order);
        AddLog(sLastTrade);
    }

    private void CloseCurrentPosition(String s, int bar)
    {
        if (s == "")
            return;

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
        globalOrder = order;
        OpenOrder(order);
    }

    protected override void OnOrderChanged(Order order)
    {
        if (order == globalOrder)
        {
            switch (order.Status())
            {
                case OrderStatus.None:
                    // The order has an undefined status (you need to wait for the next method calls).
                    break;
                case OrderStatus.Placed:
                    // the order is placed.
                    break;
                case OrderStatus.Filled:
                    // the order is filled.
                    break;
                case OrderStatus.PartlyFilled:
                    // the order is partially filled.
                    {
                        var unfilled = order.Unfilled; // this is a unfilled volume.

                        break;
                    }
                case OrderStatus.Canceled:
                    // the order is canceled.
                    break;
            }
        }
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
        sLastLog = s;
        this.LogDebug(s);
    }

    #endregion

}


using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Resources;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.ConstrainedExecution;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using System;

public class BumbleBee : ATAS.Strategies.Chart.ChartStrategy
{
    private const int LONG = 1;
    private const int SHORT = 2;
    private const String sVersion = "Beta 1.0";
    private const int ACTIVE = 1;
    private const int STOPPED = 2;

    private int iFontSize = 12;
    private int iBotStatus = ACTIVE;
    private Stopwatch clock = new Stopwatch();
    private Rectangle rc = new Rectangle() { X = 50, Y = 50, Height = 200, Width = 400 };
    private DateTime dtStart = DateTime.Now;
    private String sLastTrade = String.Empty;
    private int _lastBar = -1;
    private decimal Volume = 1;

    private readonly AwesomeOscillator _ao = new AwesomeOscillator();
    private readonly ParabolicSAR _psar = new ParabolicSAR();
    private readonly EMA fastEma = new EMA() { Period = 20 };
    private readonly EMA slowEma = new EMA() { Period = 40 };
    private readonly FisherTransform _ft = new FisherTransform() { Period = 10 };
    private readonly SuperTrend _st = new SuperTrend() { Period = 10, Multiplier = 1m };
    private readonly KAMA _kama9 = new KAMA() { ShortPeriod = 2, LongPeriod = 109, EfficiencyRatioPeriod = 9 };
    private readonly T3 _t3 = new T3() { Period = 10, Multiplier = 1 };

    public int TextFont { get => iFontSize; set { iFontSize = value; RecalculateValues(); } }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var font = new RenderFont("Calibri", iFontSize);
        var fontB = new RenderFont("Calibri", iFontSize, FontStyle.Bold);
        int upY = 50;
        int upX = 50;
        var txt = String.Empty;

        // LINE 1 - BOT STATUS + ACCOUNT + START TIME
        switch (iBotStatus)
        {
            case ACTIVE:
                TimeSpan t = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds);
                String an = String.Format("{0:D2}:{1:D2}:{2:D2}", t.Hours, t.Minutes, t.Seconds);
                txt = $"BOT ACTIVE on {TradingManager.Portfolio.AccountID} since " + dtStart.ToString() + " (" + an + ")";
                context.DrawString(txt, fontB, Color.Lime, upX, upY);
                if (!clock.IsRunning)
                    clock.Start();
                break;
            case STOPPED:
                txt = $"BOT STOPPED on {TradingManager.Portfolio.AccountID}";
                context.DrawString(txt, fontB, Color.Orange, upX, upY);
                if (clock.IsRunning)
                    clock.Stop();
                break;
        }
        var tsize = context.MeasureString(txt, fontB);
        upY += tsize.Height + 6;

        // LINE 2 - TOTAL TRADES + PNL
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

    public BumbleBee()
    {
        EnableCustomDrawing = true;
        Add(_ao);
        Add(_ft);
        Add(_psar);
        Add(_st);
        Add(_kama9);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        var pbar = bar-1;
        var prevBar = _lastBar;
        _lastBar = bar;

        if (prevBar == bar) // !CanProcess(bar) || 
            return;

        var candle = GetCandle(pbar);
        value = candle.Close;

        _t3.Calculate(pbar, value);
        fastEma.Calculate(pbar, value);
        slowEma.Calculate(pbar, value);

        var ao = ((ValueDataSeries)_ao.DataSeries[0])[pbar];
        var kama9 = ((ValueDataSeries)_kama9.DataSeries[0])[pbar];
        var kama21 = ((ValueDataSeries)_kama9.DataSeries[0])[pbar];
        var t3 = ((ValueDataSeries)_t3.DataSeries[0])[pbar];
        var fast = ((ValueDataSeries)fastEma.DataSeries[0])[pbar];
        var fastM = ((ValueDataSeries)fastEma.DataSeries[0])[pbar - 1];
        var slow = ((ValueDataSeries)slowEma.DataSeries[0])[pbar];
        var slowM = ((ValueDataSeries)slowEma.DataSeries[0])[pbar - 1];
        var f1 = ((ValueDataSeries)_ft.DataSeries[0])[pbar];
        var f2 = ((ValueDataSeries)_ft.DataSeries[1])[pbar];
        var st = ((ValueDataSeries)_st.DataSeries[0])[pbar];
        var psar = ((ValueDataSeries)_psar.DataSeries[0])[pbar];

        var t1 = ((fast - slow) - (fastM - slowM)) * 150;

        var fisherUp = (f1 < f2);
        var fisherDown = (f2 < f1);
        var psarBuy = (psar < candle.Close);
        var psarSell = (psar > candle.Close);
        var red = candle.Close < candle.Open;
        var green = candle.Close > candle.Open;
        var p1C = GetCandle(pbar - 1);
        var c1G = p1C.Open < p1C.Close;
        var c1R = p1C.Open > p1C.Close;

        var bShowDown = true;
        var bShowUp = true;

        if (psarSell || !fisherUp || value < t3 || t1 < 0 || ao < 0)
            bShowUp = false;
        if (psarBuy || !fisherDown || value > t3 || t1 >= 0 || ao > 0)
            bShowDown = false;

        if (green && CurrentPosition < 0)
            CloseCurrentPosition();
        if (red && CurrentPosition > 0)
            CloseCurrentPosition();

        if (green && c1G && candle.Open > p1C.Close)
            OpenPosition("Volume Imbalance", candle, bar, OrderDirections.Buy);
        if (red && c1R && candle.Open < p1C.Close)
            OpenPosition("Volume Imbalance", candle, bar, OrderDirections.Sell);

        if (green && bShowUp)
            OpenPosition("Standard Buy", candle, bar, OrderDirections.Buy);
        if (red && bShowDown)
            OpenPosition("Standard Sell", candle, bar, OrderDirections.Sell);
    }

    private void OpenPosition(String sReason, IndicatorCandle c, int bar, OrderDirections direction)
    {
        var sD = String.Empty;

        if (direction == OrderDirections.Buy)
        {
            sLastTrade = "Bar " + bar + " - " + sReason + " LONG at " + c.Close;
            sD = sReason + " LONG (" + bar + ")";
        }
        else
        {
            sLastTrade = "Bar " + bar + " - " + sReason + " SHORT at " + c.Close;
            sD = sReason + " SHORT (" + bar + ")";
        }

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

    private void CloseCurrentPosition()
    {
        var order = new Order
        {
            Portfolio = Portfolio,
            Security = Security,
            Direction = CurrentPosition > 0 ? OrderDirections.Sell : OrderDirections.Buy,
            Type = OrderTypes.Market,
            QuantityToFill = Math.Abs(CurrentPosition),
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

}
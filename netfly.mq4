//+------------------------------------------------------------------+
//|                                                       Netfly.mq4 |
//|                                     Copyright 2017, iiTech info. |
//|                                          https://www.iitech.info |
//+------------------------------------------------------------------+
#property copyright "Copyright 2017, iiTech info."
#property link      "https://www.iitech.info"
#property version   "1.52"
#property strict

#include<Utils.mqh>
#include<HedgeUtils.mqh>
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
enum BladeModeType
  {
   BuyAndSell,
   Buy,
   Sell,
  };
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
enum GridModeType
  {
   AverageTp,
   SingleTp
  };
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
enum PeriodType
  {
   QuarterHour=900,
   Hourly=3600,
   FourHour=14400,
   Daily=86400
  };

input int EAId=15655;
input BladeModeType BladeMode=BuyAndSell;
input int GridSpacing=50;
input int InitialGap=0;
input int MaxNetNumber=99;
input double MaxTradeLot = 0;
input int TakeProfitPoints=200;
input double TakeProfitDollars=200;
input bool IsCrawlMode=false;
input double StopLossDollars=2000;
input int MaxPendingOrders=1;
input double LotSize=0.01;
input double GridSpacingStep=0.5;
input double LotSizeIncreasePercent=16.2;
input double GridStartPriceUp=0;
input double GridStartPriceDown=0;
input int MaxOrderPeriodInSeconds=3600;
input int MaxOrderFrequency=4;
input PeriodType MaxOrderPeriodInSeconds0=Hourly;
input int MaxOrderFrequency0=4;
input PeriodType MaxOrderPeriodInSeconds1=FourHour;
input int MaxOrderFrequency1=0;
input PeriodType MaxOrderPeriodInSeconds2=Daily;
input int MaxOrderFrequency2=0;
input bool IsRestartAllowed = true;
input int RecoverRoundId=0; //
input double LastBid = 0;
input double LastAsk = 0;
input bool IsStartAtDayOpen=false;
input int StopHours=99;
input int StopMinutes=30;
input int StartBackTestDayCount = 10;
//+------------------------------------------------------------------+
//| Expert initialization function                                   |
//+------------------------------------------------------------------+

string _prefix="netfly_";
int _initialNetNumber=5;
double _weightMatrix[200],_netBuyPrice[200],_netSellPrice[200];
double _tickSize,_lastModifiedPrice;
int _roundCount=0;
double _roundPnL=0;
int _stopLevel=0;
double _startBuyPrice=999,_startSellPrice=999;
int _nextIdx=-1;
double _nextPriceLevel=0,_nextLotSize=0;
int _orderIds[1000];
int _orderIdx=0;
int _currentDay=-1;
double _lastDayEquity=0;
double _lastDayMinimumEquity=0;
int _runningDayCount = 0;
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
int OnInit()
  {
   ObjectsDeleteAll();
   _tickSize=MarketInfo(Symbol(),MODE_TICKSIZE);
   _stopLevel=MarketInfo(Symbol(),MODE_STOPLEVEL);
   SetupWeight();
   CreateControls();

   if(RecoverRoundId!=0 && LastAsk!=0 && LastBid!=0)
     {
      _roundCount=RecoverRoundId;
      SetUpLevels(LastBid,LastAsk);
     }

//  OnTick();
   return(INIT_SUCCEEDED);
  }
//+------------------------------------------------------------------+
//| Expert deinitialization function                                 |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
  {
//ObjectsDeleteAll();
   ChartRedraw();
   Print(__FUNCTION__,"_Uninitalization reason code = ",reason);
//--- The second way to get the uninitialization reason code
//Print(__FUNCTION__,"_UninitReason = ",getUninitReasonText(_UninitReason));
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CreateControls()
  {
   CreateButton("_btnEntry","No More Orders","Allow Orders",180,120,120,18,CORNER_RIGHT_LOWER,clrOrangeRed,clrBlack);
   CreateButton("_btnMaunal","Manual: Enter","Switch to Manual",180,140,120,18,CORNER_RIGHT_LOWER,clrOrangeRed,clrBlack);
  }
//+------------------------------------------------------------------+
//| Expert tick function                                             |
//+------------------------------------------------------------------+
void OnTick()
  {

   CreateControls();

   _roundPnL=GetPnL(EAId,_roundCount);
   ShowInfo(EAId,_roundCount);

   _lastDayMinimumEquity=MathMin(AccountEquity(),_lastDayMinimumEquity);

   if(IsTradeAllowed()==false)
     {
      Comment("AutoTrading is not enabled.");
      return;
     }


   if(IsTesting() || IsOptimization())
     {
      OnNewDay();

      if(_runningDayCount < StartBackTestDayCount)
        {
         Print("Current Day NO: " + _runningDayCount + " / " + StartBackTestDayCount);
         return;
        }
     }

   DoBiz();
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void OnNewDay()
  {
   if(_currentDay!=DayOfYear())
     {

      _runningDayCount++;
      _currentDay=DayOfYear();

      double equityChange= _lastDayMinimumEquity - _lastDayEquity;

      string row=TimeToStr(TimeCurrent(),TIME_DATE|TIME_SECONDS)+","+DoubleToStr(AccountBalance(),2)+","+DoubleToStr(AccountEquity(),2)+","+DoubleToStr(equityChange,2)+"\r\n";

      _lastDayEquity=AccountEquity();
      _lastDayMinimumEquity=AccountEquity();

      string fileName = Symbol() + "_" + StartBackTestDayCount + ".csv";

      int handle=FileOpen(fileName,FILE_READ|FILE_WRITE|FILE_CSV);
      if(handle!=INVALID_HANDLE)
        {
         FileSeek(handle,0,SEEK_END);
         FileWriteString(handle,row,StringLen(row));
         FileClose(handle);
        }
     }

  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
double GetPnL(int eaId_,int roundId_)
  {
   double pnl=0;

   if(IsTesting() || IsOptimization())
     {
      for(int i=0; i<_orderIdx; i++)
        {
         if(OrderSelect(_orderIds[i],SELECT_BY_TICKET))
           {
            pnl+=OrderProfit()+OrderSwap()+OrderCommission();
           }
        }

      return pnl;
     }

   string comment=eaId_+"-"+roundId_;
   for(int i=OrdersHistoryTotal()-1; i>=0; i--)
     {
      if(OrderSelect(i,SELECT_BY_POS,MODE_HISTORY) && OrderType()<=1
         && OrderMagicNumber()==eaId_ && StringFind(OrderComment(),comment)!=-1)
        {
         pnl+=OrderProfit()+OrderSwap()+OrderCommission();

         if(TimeCurrent()-OrderCloseTime()>3600*24*30)
           {
            break;
           }
        }
     }

   for(int i=0; i<OrdersTotal(); i++)
     {
      if(OrderSelect(i,SELECT_BY_POS) && OrderMagicNumber()==eaId_ && StringFind(OrderComment(),comment)!=-1)
        {
         pnl+=OrderProfit()+OrderSwap()+OrderCommission();
        }
     }

   return pnl;
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+



//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void ShowInfo(int eaId_,int roundId_)
  {
   string comment=eaId_+"-"+roundId_;
   string infoLabels[100];
   infoLabels[0]="---------- NetFly ----------";
   infoLabels[1]="Buy  #\t" + DoubleToStr(GetOrderCountByMagicNumber(Symbol(),OP_BUY, EAId),0) + " \tLot: " + DoubleToStr(GetTotalPositionByMagicNumber(Symbol(),OP_BUY, EAId),2) + " Avg: ["+DoubleToStr(GetAvgPrice(OP_BUY,EAId),2)+"]";
   infoLabels[2]="Sell  #\t" + DoubleToStr(GetOrderCountByMagicNumber(Symbol(),OP_SELL, EAId),0) + " \tLot: " + DoubleToStr(MathAbs(GetTotalPositionByMagicNumber(Symbol(),OP_SELL, EAId)),2) + " Avg: ["+DoubleToStr(GetAvgPrice(OP_SELL,EAId),2)+"]";
   infoLabels[3]="Current Round:" + _roundCount + " " +  " PnL: " + DoubleToStr(_roundPnL,2);

//Print(OrdersHistoryTotal()  + "|" + OrdersTotal());
   if(false)
     {
      int x=4,y=0;
      for(int i=0; i<OrdersHistoryTotal(); i++)
        {
         if(OrderSelect(i,SELECT_BY_POS,MODE_HISTORY) && OrderMagicNumber()==eaId_ && StringFind(OrderComment(),comment)!=-1)
           {
            infoLabels[x++]=OrderTicket()+" "+OrderType()+" "+OrderOpenTime()+" "+OrderOpenPrice()+" "+OrderCloseTime()+" "+OrderProfit();
           }
        }

      for(int i=0; i<OrdersTotal(); i++)
        {
         if(OrderSelect(i,SELECT_BY_POS) && OrderMagicNumber()==eaId_ && StringFind(OrderComment(),comment)!=-1)
           {
            infoLabels[x+(y++)]=OrderTicket()+" "+OrderType()+" "+OrderOpenTime()+" "+OrderOpenPrice()+" "+OrderCloseTime()+" "+OrderProfit();
           }
        }
     }

   CreateLabels(infoLabels);
   DrawDebugLevels(MaxNetNumber);
   ChartRedraw();
  }
//+------------------------------------------------------------------+
//| ChartEvent function                                              |
//+------------------------------------------------------------------+
void OnChartEvent(const int id,
                  const long &lparam,
                  const double &dparam,
                  const string &sparam)
  {
//---

  }
//+------------------------------------------------------------------+

//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void SetupWeight()
  {
   for(int i=0; i<100; i++)
     {
      if(LotSizeIncreasePercent==0)
        {
         _weightMatrix[i]=1;
        }
      else
        {
         _weightMatrix[i]=MathPow(1+LotSizeIncreasePercent*0.01,i);
         //Print(_weightMatrix[i]);
        }
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void ClearArray(int &array_[])
  {
   for(int i=0; i<100; i++)
     {
      if(array_[i]==NULL)
        {
         break;
        }
      array_[i]=NULL;
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void ClearArray(double &array_[])
  {
   for(int i=0; i<100; i++)
     {
      if(array_[i]==NULL)
        {
         break;
        }
      array_[i]=NULL;
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+

//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void SetUpLevels(double bid_,double ask_)
  {

   ClearArray(_netBuyPrice);
   ClearArray(_netSellPrice);
   ClearArray(_orderIds);

   double midPrice=0.5 *(bid_+ask_);

   if(BladeMode==BuyAndSell)
     {
      _netBuyPrice[0]=midPrice-0.5*GridSpacing*_tickSize;
      _netSellPrice[0]=midPrice+0.5*GridSpacing*_tickSize;
      for(int i=1; i<=MaxNetNumber; i++)
        {
         _netBuyPrice[i]=_netBuyPrice[i-1]-(GridSpacing+GridSpacingStep*i)*_tickSize;
         _netSellPrice[i]=_netSellPrice[i-1]+(GridSpacing+GridSpacingStep*i) *_tickSize;
        }
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CastNewNet(double bid_,double ask_)
  {

   if(GetPendingOrdersTotal(Symbol(),EAId)>0)
     {
      return;
     }

   if(!IsStartAtDayOpen)
      SetUpLevels(bid_,ask_);

   Print("NEW GRID: "+bid_+"/"+ask_);

   _roundPnL=0;
   _lastModifiedPrice=0;
   _orderIdx=0;
   _roundCount++;

//DrawDebugLevels();

   for(int i=0; i<MaxPendingOrders; i++)
     {
      string comment=(i==0? "00": IntegerToString(i));
      comment=EAId+"-"+_roundCount+"-"+comment;
      double weight=NormalizeDouble(LotSize*_weightMatrix[i],2);

      double tp=0;

      tp=_netBuyPrice[i]+TakeProfitPoints*_tickSize;

      int ticket=PlaceLimitOrder(NULL,OP_BUYLIMIT,_netBuyPrice[i],weight,0,tp,comment,EAId);
      _orderIds[_orderIdx++]=ticket;

      //int ticket=PlaceMarketOrder(NULL,OP_BUY,weight,0,0,comment,EAId);

      tp=_netSellPrice[i]-TakeProfitPoints*_tickSize;
      //ticket=PlaceMarketOrder(NULL,OP_SELL,weight,0,2,comment,EAId);
      ticket=PlaceLimitOrder(NULL,OP_SELLLIMIT,_netSellPrice[i],NormalizeDouble(LotSize*_weightMatrix[i],2),0,tp,comment,EAId);
      _orderIds[_orderIdx++]=ticket;

     }

  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void DrawDebugLevels(int i)
  {
   if(ObjectFind(0,"b"+i)>=0)
     {
      HLineMove(0,"b"+i,_netBuyPrice[i]);
     }
   else
     {
      HLineCreate(0,"b"+i,0,_netBuyPrice[i],clrWheat);
     }

   if(ObjectFind(0,"s"+i)>=0)
     {
      HLineMove(0,"s"+i,_netSellPrice[i]);
     }
   else
     {
      HLineCreate(0,"s"+i,0,_netSellPrice[i],clrWheat);
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void CloseNet()
  {
   if(_roundPnL>TakeProfitDollars)
     {
      Print("Round #"+_roundCount+" PnL: "+_roundPnL);
      CloseAllPositionAndDeleteAllOrders("",EAId);
      VLineCreate(0,_roundCount+":"+_roundPnL,0,TimeCurrent(),clrGreen,STYLE_SOLID,3);

     }

   if(_roundPnL<-1*StopLossDollars)
     {
      Print("Round #"+_roundCount+" PnL: "+_roundPnL);
      CloseAllPositionAndDeleteAllOrders("",EAId);
      VLineCreate(0,_roundCount+":"+_roundPnL,0,TimeCurrent(),clrRed,STYLE_SOLID,3);
     }

  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
bool IsDuringAllowedTradingSesstion()
  {
   if(Hour()>=StopHours && Minute()>=StopMinutes)
     {
      return false;
     }

   return true;
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void DoBiz()
  {

   CloseNet();
   MoveOrderCloser(OP_BUYLIMIT);
   MoveOrderCloser(OP_SELLLIMIT);

   if(IsStartAtDayOpen)
     {
      double open=iOpen(NULL,PERIOD_D1,0);
      SetUpLevels(open,open);
     }

   if((ObjectGetInteger(0,"_btnEntry",OBJPROP_STATE)==false && IsDuringAllowedTradingSesstion()) || ObjectGetInteger(0,"_btnEntry",OBJPROP_STATE)==true)
     {
      if(GetPendingOrdersTotal(Symbol(),EAId)==0 && GetWorkingOrdersTotal(Symbol(),EAId)==0 && IsRestartAllowed)
        {
         CastNewNet(Bid,Ask);
        }

      FixGrid(OP_BUYLIMIT);
      FixGrid(OP_SELLLIMIT);
     }

  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void MoveOrderCloser(int op_)
  {
   string commentSuffix=EAId+"-"+_roundCount+"-";

   int pendingOrderCount=GetOrdersTotal(op_,EAId);

   double price = op_ == OP_BUYLIMIT? Ask:Bid;
   int multiple = op_ == OP_BUYLIMIT? 1:-1;

   if(pendingOrderCount==0)
     {
      return;
     }

   if(pendingOrderCount>MaxPendingOrders)
     {
      CancelAllOrder(op_,EAId);
     }

   int min=-1;
   if(pendingOrderCount==MaxPendingOrders)
     {
      min=GetMaxComment(op_,EAId);
     }

   if(min<1)
     {
      return;
     }

   bool b=false;
   if(op_==OP_BUYLIMIT)
     {
      b=price>=_netBuyPrice[min -1]+_stopLevel*_tickSize;
     }

   if(op_==OP_SELLLIMIT)
     {
      b=price<_netSellPrice[min-1]-_stopLevel*_tickSize;
     }

   string ii=(min -1==0? "00": IntegerToString(min -1));
   bool isBuy=op_==OP_BUYLIMIT?true:false;
   int isOrderExist=FindOrderBySymbolAndComment(Symbol(),commentSuffix+ii,isBuy);

   if(b && isOrderExist==-1)
     {
      CancelAllOrder(op_,EAId);
      Print("Cancel Max Grid Id: "+min);
     }

  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+

//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
void FixGrid(int op_)
  {
   string commentSuffix=EAId+"-"+_roundCount+"-";
   int pendingOrderCount=GetOrdersTotal(op_,EAId);
   double price = op_ == OP_BUYLIMIT? Ask:Bid;
   int multiple = op_ == OP_BUYLIMIT? 1:-1;
   bool outOfRange=op_==OP_SELLLIMIT ? Bid<_netSellPrice[MaxNetNumber]: Ask>=_netBuyPrice[MaxNetNumber];

   int count=op_==OP_BUYLIMIT ? GetOrderCountByMagicNumber(Symbol(),OP_BUY,EAId): GetOrderCountByMagicNumber(Symbol(),OP_SELL,EAId);

   if(count>=MaxNetNumber)
     {
      return;
     }

   if(pendingOrderCount<MaxPendingOrders && outOfRange)
     {
      //find start idx to place order
      int nextId=-1;
      price=op_==OP_BUYLIMIT? Ask:Bid;
      for(int i=0; i<=MaxNetNumber; i++)
        {
         bool b=false;
         if(op_==OP_BUYLIMIT)
           {
            b=price>=_netBuyPrice[i]+_stopLevel*_tickSize;
           }

         if(op_==OP_SELLLIMIT)
           {
            b=price<_netSellPrice[i]-_stopLevel*_tickSize;
           }

         string ii=(i==0? "00": IntegerToString(i));
         bool isBuy=op_==OP_BUYLIMIT?true:false;
         if(b && FindOrderBySymbolAndComment(Symbol(),commentSuffix+ii,isBuy)==-1)
           {
            nextId=i;
            //Print("Round - nextId: "+_roundCount+" - "+nextId+"/"+op_+" pending: "+pendingOrderCount);
            break;
           }
        }

      if(nextId>=0)
        {
         for(int i=0; i<MaxPendingOrders; i++)
           {
            int idx=nextId+i;
            double entry=op_==OP_SELLLIMIT ? _netSellPrice[idx]:_netBuyPrice[idx];
            bool inRange=op_==OP_SELLLIMIT ? Bid<entry : Ask>=entry;

            double weight=IsCrawlMode? _weightMatrix[count+1]: _weightMatrix[idx];

            if(inRange && IsAllowedToOrder(op_,EAId))
              {
               double tp=0;
               tp=entry+multiple*TakeProfitPoints*_tickSize;

               string ii=(idx==0? "00": IntegerToString(idx));
               
               double lotsize = NormalizeDouble(LotSize*weight,2);             
               lotsize = MaxTradeLot == 0 ? lotsize: MathMin(lotsize, MaxTradeLot);
               
               int ticket=PlaceOrder(Symbol(),op_,lotsize,entry,3,0,tp,commentSuffix+ii,EAId);
               _orderIds[_orderIdx++]=ticket;

              }

            if(!IsAllowedToOrder(op_,EAId))
              {
               //Print("Failed to create: "+idx+" side: "+op_);
               //VLineCreate(0,TimeToStr(TimeCurrent()),0,TimeCurrent(),clrRed,STYLE_SOLID,3);
              }
            pendingOrderCount=GetOrdersTotal(op_,EAId);
           }
        }
     }
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
bool IsAllowedToOrder(int op_,int magicNo_)
  {
   return IsAllowedToOrder(op_, magicNo_, MaxOrderPeriodInSeconds0, MaxOrderFrequency0)
          && IsAllowedToOrder(op_,magicNo_,MaxOrderPeriodInSeconds1,MaxOrderFrequency1)
          && IsAllowedToOrder(op_,magicNo_,MaxOrderPeriodInSeconds2,MaxOrderFrequency2);
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
bool IsAllowedToOrder(int op_,int magicNo_,PeriodType periodType_,int frequency_)
  {
   if(frequency_==0)
     {
      return true;
     }

   int count = 0;
   for(int i = OrdersTotal() - 1; i >= 0; i --)
     {
      if(OrderSelect(i,SELECT_BY_POS) && OrderMagicNumber()==magicNo_ && OrderOpenTime()>=TimeCurrent()-periodType_)
        {

         if(OrderOpenTime()<TimeCurrent()-periodType_)
           {
            return (count < frequency_);
           }

         if((op_%2==0 && OrderType()%2==0) || (op_%2==1 && OrderType()%2==1))
           {
            count++;
           }

         if(count>=frequency_)
           {
            return false;
           }
        }
     }
   return true;
  }
//+------------------------------------------------------------------+
//|                                                                  |
//+------------------------------------------------------------------+
int GetMaxComment(int op_,int magicNo_)
  {
   int max=0;
   for(int i=0; i<OrdersTotal(); i++)
     {
      if(OrderSelect(i,SELECT_BY_POS) && OrderType()==op_ && OrderMagicNumber()==magicNo_)
        {
         string comment=OrderComment();
         string token=GetToken(comment,3,2,"-");
         int value=StrToInteger(token);
         max=MathMax(max,value);
        }
     }
   return max;
  }
//+------------------------------------------------------------------+
//+------------------------------------------------------------------+
//| Create the vertical line                                         |
//+------------------------------------------------------------------+
bool VLineCreate(const long            chart_ID=0,        // chart's ID
                 const string          name="VLine",      // line name
                 const int             sub_window=0,      // subwindow index
                 datetime              time=0,            // line time
                 const color           clr=clrRed,        // line color
                 const ENUM_LINE_STYLE style=STYLE_SOLID, // line style
                 const int             width=1,           // line width
                 const bool            back=false,        // in the background
                 const bool            selection=true,    // highlight to move
                 const bool            hidden=true,       // hidden in the object list
                 const long            z_order=0)         // priority for mouse click
  {
//--- if the line time is not set, draw it via the last bar
   if(!time)
      time=TimeCurrent();
//--- reset the error value
   ResetLastError();
//--- create a vertical line
   if(!ObjectCreate(chart_ID,name,OBJ_VLINE,sub_window,time,0))
     {
      Print(__FUNCTION__,
            ": failed to create a vertical line! Error code = ",GetLastError());
      return(false);
     }
//--- set line color
   ObjectSetInteger(chart_ID,name,OBJPROP_COLOR,clr);
//--- set line display style
   ObjectSetInteger(chart_ID,name,OBJPROP_STYLE,style);
//--- set line width
   ObjectSetInteger(chart_ID,name,OBJPROP_WIDTH,width);
//--- display in the foreground (false) or background (true)
   ObjectSetInteger(chart_ID,name,OBJPROP_BACK,back);
//--- enable (true) or disable (false) the mode of moving the line by mouse
//--- when creating a graphical object using ObjectCreate function, the object cannot be
//--- highlighted and moved by default. Inside this method, selection parameter
//--- is true by default making it possible to highlight and move the object
   ObjectSetInteger(chart_ID,name,OBJPROP_SELECTABLE,selection);
   ObjectSetInteger(chart_ID,name,OBJPROP_SELECTED,selection);
//--- hide (true) or display (false) graphical object name in the object list
   ObjectSetInteger(chart_ID,name,OBJPROP_HIDDEN,hidden);
//--- set the priority for receiving the event of a mouse click in the chart
   ObjectSetInteger(chart_ID,name,OBJPROP_ZORDER,z_order);
//--- successful execution
   return(true);
  }
//+------------------------------------------------------------------+

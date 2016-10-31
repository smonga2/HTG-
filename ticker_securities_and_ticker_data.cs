
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using System.Security.Cryptography.X509Certificates;
using Silexx.Safire.Core.Data.Requests;
using Silexx.Safire.Core.Data.Securities;
using Silexx.Safire.Core.Data.MarketData;
using System.Threading;
using Silexx.Safire.Core.Data;
using Silexx.Safire.Core.Data.Trading;
using Silexx.Safire.Core.Data.Trading.OrderInstructions;
using Silexx.Safire.Core.OptionPricing;
using Silexx.Safire.API;
using Silexx.Safire.Core.Data.Types;


namespace ConsoleApplication1
{

    
    class ticker_securities_and_ticker_data
    {
       // private Stock underlying;
        public bool has_this_ticker_started_trading = false; //this is a reference entity will trun to true once the start button is clicked fromm the GUI..this is so that incase start button is pressed twice it doesnt start trading twice
        public int orders_per_symbol = 0;
        private static Client client; //client
        private string _underly;  //underlying ticker
        public Security security;
        public bool isToxicSymbol = false;
        private double Stock_Last_Bid = 0;
        private double Stock_Last_Ask = 0;
        private OptionChain optionChain; //option chain for ticker
        private int total_stock_inventory = 0;
        public bool isUnderlyingLoaded = false; //used in constructor and updated by initi_quoter..check whether algorithm has started for this ticker
        public RebateTrader _rebateTrader; //takes static form RebbateTrader as reference..independent of ticker..used in constructor... used to extract parameters from the FORM to execute algorithm
      
        public Dictionary<Option, individual_security_params> _optionState = new Dictionary<Option, individual_security_params>(); //updated in initiQuoter function
        public ticker_securities_and_ticker_data(RebateTrader rebateTrader, Client _client, string underly) //CONSTRUCTOR.. initiQuoter is called here..only parameters are set
        {
            _rebateTrader = rebateTrader;
            client = _client;
            _underly = underly;

            initiQuoter();
            
        }
        private void initiQuoter()//initQuoter is boolean and returns true if even a single option for the underlying is shortlisted into the pre selected universe and the function also updates _optionState dictionary
                                  //keep looping initiQuoter until stop command is called i.e. if(selected symbol == any symbol in list and stop button pressed stop strategy for this ticker)
                                  //have similar button for contract

        {

            //can bypass loading security in first place to speed code up.. need to determine how to do this though
          //  Console.WriteLine("started on " + _underly);
            security = client.GetSecurity(_underly);

            if (security == null)
            {
                Console.WriteLine("Failed to load data on "+_underly);

                return;
            }

            optionChain = client.GetOptionChain(security);

            if (optionChain == null)
            {
                Console.WriteLine("Option chain not present for "+_underly);

                return;
            }
            

            foreach (var option in optionChain.Options)
            
                initialize_list(option);
            
            client.SubscribeSecurities(_optionState.Keys.ToList().ConvertAll(x => x as Security));
           
        }
        private void initialize_list(Option _option)
        {
            
            var day2Expire = (_option.Expiration - DateTime.Today).TotalDays;
            
            if (!(day2Expire > RebateTrader.minDay2Expiration && day2Expire < RebateTrader.maxDay2Expiration))//isBATS &&
            
                return;
            
            individual_security_params st = (new individual_security_params(_option as Option,this)); // do not comment this line
            
                _optionState.Add(_option, st);
            
        }
        public void trading_button_click()
        {

            Parallel.ForEach(_optionState.Values, _state => start_trading(_state._option,_state));

            
        } //activates when start button is clicked on the GUI
        
        #region -------------opening and closing functions functions----------

        public  void Initial_condition_and_order(individual_security_params _state)//first function when start trading is invoked
        {
            
            do
            {
                
                if (CheckEntryCriteria(_state, _state._option))
                {
                    #region  begin the new probe: may save data later ..

                    if (_state.wave == 0) // means the NBBO not changed, prob higher
                        {
                           
                            _state.wave = 1;
                        
                        sendWave(_state._option, _state);
                        
                    }

                        else if (_state.wave >= 1)                     
                        {
                            if (_state.isPartialFill)                     
                            {
                                
                                 sendWave(_state._option, _state);
                                // probe same wave


                            }
                            else if (_state.isOpnWaveComplete)                 
                            {
                                if (_state.wave < 4)                           
                                    _state.wave++;
                                
                                  sendWave(_state._option, _state);

                            }
                        }
                        #endregion       
                }
                else
                {
                    _state.wave = 0;
                    _state.probeSide = 0;
                    _state.sixEntryChecks = 0;
                    _state.DeltaCheck = false;
                    _state.NBBOSpread = false;
                    _state.UnderlySpread = false;
                    _state.TimeNBBO = false;
                    _state.CritSize = false;
                    _state.BookRatio = false;
                    RebateTrader.isPassiveandTrading--;
                    return;
                }
                
                #region  reset all params
                
                _state.isHedging = false;
                _state.isScratching = false;
                _state.isStockHedged = false;
                _state.sixEntryChecks = 0;
                _state.DeltaCheck = false;
                _state.NBBOSpread = false;
                _state.UnderlySpread = false;
                _state.TimeNBBO = false;
                _state.CritSize = false;
                _state.BookRatio = false;
                _state.closing_attempt = 1;
                _state.hold_flash_thread.Reset();
                _state.hold_opening_thread.Reset();
                _state.closing_thread.Reset();
                #endregion

            } while (_state.wave > 0);

            _state.isPartialFill = false;
            _state.isOpnWaveComplete = false;
            RebateTrader.isPassiveandTrading--;
            
        }
     
        private void closing_wave(Option option,individual_security_params _state)
        {
            int CritSZ = 0;
            switch (_state.wave)
            {
                case 1:
                    CritSZ = (int)RebateTrader.ScratchSZ.ScSZ1;
                    break;
                case 2:
                    CritSZ = (int)RebateTrader.ScratchSZ.ScSZ2;
                    break;
                case 3:
                    CritSZ = (int)RebateTrader.ScratchSZ.ScSZ3;
                    break;
                case 4:
                    CritSZ = (int)RebateTrader.ScratchSZ.ScSZ4;
                    break;
            }

            close_one_tick_lower(option, _state, CritSZ);

            if (_state.closing_attempt >= 500 || _state.isScratching) //order failed to complete // conditions were violated and scrathing was not completed.. continue with trying to hedge.. second condition is reduandant... bt makes code criteria more robust
            {
                
                _state.wave = 0;
                StockHedge(option, _state);
                ClosingStrategy(_state, option);
                
            }

            else if ( _state.isHedging) //conditions were violated while trying to close one tick lower and the option is completly hedged with stock
            
                ClosingStrategy(_state, option);
            
            _state.closing_thread.WaitOne();
            _state.closing_thread.Reset();

            _state.isStockClosing = false;
            _state.isStockHedged = false; // will not restart trading unless all stock is removed for this strike
        }

        #endregion

        #region -------------- Entry checks-----------
        private bool CheckEntryCriteria(individual_security_params _state, Option op)
        {
            
            #region check side of order..  +1 for buy side or -1 for sell side 0 if neither
            
            
            _state.probeSide = (_state.last_trade <= (_state.last_Ask+_state.last_bid)/2)? RebateTrader.buy_open? 1:0 : RebateTrader.sell_open ? -1 : 0;

            if (_state.probeSide == 0)
                
                return false;
            
            #endregion


            _state.entry_at_NBBO = _state.probeSide == 1 ? (_state.last_trade == _state.last_bid ? true : false) : (_state.last_trade == _state.last_Ask ? true : false);

            #region minimum number of exhanges on entry side
            //List<string> exchanges = new List<string>();
            //client.AddDepthQuoteUpdateHandler(op, delegate (Security s, DepthQuote q)
            //{
            //    if (_state.probeSide == 1 && q.DepthQuoteType == DepthQuoteType.Bid && !exchanges.Contains(q.MMID))
            //    {


            //        exchanges.Add(q.MMID);
            //    }
            //    if (_state.probeSide == -1 && q.DepthQuoteType == DepthQuoteType.Ask && !exchanges.Contains(q.MMID))
            //    {



            //        exchanges.Add(q.MMID);
            //    }

            //});


            //client.SubscribeDepth((op as Security), Feed.OPRA_REGIONALS);
            //Thread.Sleep(400);
            //client.UnsubscribeDepth((op as Security), Feed.OPRA_REGIONALS);


            //if (exchanges.Count() < RebateTrader.minNBBOExchg)
            //{
            //    _state.wave = 0;
            //    _state.isOn = false;
            //    return false;
            //}
            #endregion

            //commented for now
            #region check minimum theo edge... difference between our price and mid market.. differentt for nickel and penny symbols


            //if ((int)((_state.new_Ask - _state.new_Bid) / (2 * op.MinimumTick)) < _state.minClsTheoEdge_penny && op.MinimumTick == 0.01
            //    || (int)((_state.new_Ask - _state.new_Bid) / (2 * op.MinimumTick)) > _state.minClsTheoEdge_nickel && op.MinimumTick == 0.05)
            //{

            //    _state.isOn = false;

            //    return false;

            //}



            #endregion

            new Thread(() => CheckDelta(_state, op)).Start();
            
            new Thread(() => CheckNBBOSpread(_state, op)).Start();

            new Thread(() => UnderlySpread(_state, op)).Start(); ;

            //Thread TimeNBBO = new Thread(() => TimeStableNBBO(_state));
            //TimeNBBO.Start();
            new Thread(() => CheckCriticalSZ(_state, op)).Start(); ;
            
            new Thread(() => CheckBookRatio(_state, op)).Start();
            
            
            while (_state.sixEntryChecks < 5)
                Thread.Sleep(20);

            
            return (_state.DeltaCheck && _state.NBBOSpread   && _state.CritSize && _state.BookRatio && _state.UnderlySpread);

            //&& _state.TimeNBBO    && 
            
        }
        private void CheckBookRatio(individual_security_params _state, Security op)//used.. self explanatory
        {
         
            if (_state.probeSide == 1)
                _state.BookRatio = _state.bid_size == 0 || (_state.ask_size / _state.bid_size) < RebateTrader.maxNBBOImbalance;
            else 

                _state.BookRatio = _state.ask_size == 0 || (_state.bid_size / _state.ask_size) < RebateTrader.maxNBBOImbalance;

            _state.sixEntryChecks++;
            
        }
        private void CheckCriticalSZ(individual_security_params _state, Security op)//check whether critical size condition is satisfied on Bid/Ask side
        {
            
            int SZ = 0;
            if (_state.probeSide == 1)
            {
                SZ = _state.bid_size;
            }
            else if (_state.probeSide == -1)
            {
                SZ = _state.ask_size;
            }
            _state.CritSize = false;

            switch (_state.wave)
            {
                case 0:
                    if (SZ >= (int)RebateTrader.CriticalSZ.CritSZ1)
                        _state.CritSize = true;

                    break;
                    
                case 1:
                    if (SZ >= (int)RebateTrader.CriticalSZ.CritSZ1)
                        _state.CritSize = true;

                    break;
                case 2:
                    if (SZ >= (int)RebateTrader.CriticalSZ.CritSZ2)
                        _state.CritSize = true;

                    break;
                case 3:
                    if (SZ >= (int)RebateTrader.CriticalSZ.CritSZ3)
                        _state.CritSize = true;

                    break;
                case 4:
                    if (SZ >= (int)RebateTrader.CriticalSZ.CritSZ4)
                        _state.CritSize = true;

                    break;
                default:
                    break;
            }

          
            
            _state.sixEntryChecks++;
            
        }
        private bool CheckBuySell(individual_security_params _state)//not used this
        {
            // var validity = _rebateTrader.disabledBox.Items.Contains(security.Symbol);
            //if (!validity)
            //    return false;

            //if (_state.probeSide == 1)
            //{
            //    validity = RebateTrader.isBUYOpening;
            //}
            //else if (_state.probeSide == -1)
            //{
            //   validity = RebateTrader.isSELLOpening;
            //}

            return false;
        }
        private bool CheckRisk(Option op, int ordSZ, int _state_wave)// used very few risk parameters
        {
        
            
            // is it greater than max order size?
             if (!((_state_wave == 1 && ordSZ <= RebateTrader._maxWave1) 
                || (_state_wave == 2 && ordSZ <= RebateTrader._maxWave2) 
                || (_state_wave == 3 && ordSZ <= RebateTrader._maxWave3)
                || (_state_wave == 4 && ordSZ <= RebateTrader._maxWave4)))
            return false;

            //OptionChain optionChain = client.GetOptionChain(op.Underlying);

            var only_option_posiitons = client.Portfolio.GetPositions().Where(x => x.Security.Symbol.FirstOrDefault() == '.' && x.Account.ToString()== "AVT4" && x.NetQty !=0);
            //only options

            var total_options_open_for_current_underlying = only_option_posiitons.Where(x => ((x.Security as Option).Underlying == op.Underlying));
            //options only on underlying under consideration
            var total_options_open_for_current_strike = total_options_open_for_current_underlying.Where(x => ((x.Security as Option).Strike == op.Strike));
            //options on current strike under consideration

            //totoal for symbol delta theta vega
            double delta = 0;
            double vega = 0;
            double theta = 0;

            if (total_options_open_for_current_underlying == null)
                return true;   //no positions
            // max parameters per symbol
            foreach (var position in total_options_open_for_current_underlying)
            {

                var option_params = _optionState.FirstOrDefault(x => x.Key.Symbol == position.Symbol).Value;
                
                    delta = delta + option_params.posSZ * option_params.option_greeks.Delta * 100 * Stock_Last_Ask;
                    theta = theta + option_params.posSZ * option_params.option_greeks.Theta * 100;
                    vega = theta + option_params.posSZ * option_params.option_greeks.Vega * 100;
                
            }

            if (!(Math.Abs(delta) < RebateTrader.maxDollarDelta_symbol && Math.Abs(theta) < RebateTrader.maxTheta_symbol && Math.Abs(vega) < RebateTrader.maxDollarVega_symbol))
            
                return false;
            

            delta = 0;
            vega = 0;
            theta = 0;

            //max parameters for strike

            if (total_options_open_for_current_strike == null)
                return true;

            foreach (var position in total_options_open_for_current_strike)
            {
                
                var option_params = _optionState.FirstOrDefault(x => x.Key.Symbol == position.Symbol).Value;
                
                
                    delta = delta + option_params.posSZ * option_params.option_greeks.Delta * 100 * Stock_Last_Ask;
                    theta = theta + option_params.posSZ * option_params.option_greeks.Theta * 100;
                    vega = theta + option_params.posSZ * option_params.option_greeks.Vega * 100;
                
            }

            if (!(Math.Abs(delta) < RebateTrader.maxDollarDelta_strk && Math.Abs(theta) < RebateTrader.maxTheta_strk && Math.Abs(vega) < RebateTrader.maxDollarVega_strk))
            
                return false;
            




            //// across full portfolio
            //delta = 0;
            //vega = 0;
            //theta = 0;

            //foreach (var position in only_option_posiitons)
            //{
            //    var ranadom_underlying = (position.Security as Option).Underlying as Security;
            //    client.GetMarketDataSnapshot(ranadom_underlying);
            //    var option_params = total_options_selected.FirstOrDefault(x => x.Key.Symbol == position.Symbol).Value;
            //    delta = delta + option_params.posSZ * option_params.option_greeks.Delta * 100 * ranadom_underlying.Quote.Ask;
            //    theta = theta + option_params.posSZ * option_params.option_greeks.Theta * 100;
            //    vega = theta + option_params.posSZ * option_params.option_greeks.Vega * 100;
            //}

            //if (!(delta < Math.Abs(_rebateTrader.maxNetDollarDelta) && theta < Math.Abs(_rebateTrader.maxNetTheta) && vega < Math.Abs(_rebateTrader.maxNetDollarVega)))
            //{
            //    isSafe = false;
            //    return isSafe;

            //};
            
            return true;

        }
        private bool CheckOrder(Option op, int ordSZ) //checks max orders total orders out and orders out for given symbol //check from silexx
        {
            
            return (
                ordSZ <= RebateTrader.maxOrderSZ
                && RebateTrader.total_orders <= RebateTrader.maxTotalOrder
                && orders_per_symbol <= RebateTrader.maxOrdr_symbol
                ); 
        }
        private bool distance(Security op, double low_prcLevel, double high_prcLevel)//determines wheter last trade happened at or one tick above above NBBO(below NBBO for sell side) and returns true if condition is satisfied 
        {


            int tick = (int)((high_prcLevel - low_prcLevel) / op.MinimumTick);

            return (tick <= 1 && tick >= 0) ? true : false;
            //return ( tick >= 0) ? true : false;


        }
        private void TimeStableNBBO(individual_security_params _state)
        {

            #region check for minimum time for  stable NBBO

            lock (_state.Quotes)
            {
                if (DateTime.Now.Millisecond - _state.Quotes.Last(x=> _state.probeSide==1? x.Bid==_state.last_bid:x.Ask==_state.last_Ask).QuoteTime.Millisecond < RebateTrader.minTmNBBOStable)
                    Thread.Sleep(DateTime.Now.Millisecond - _state.Quotes.FirstOrDefault().QuoteTime.Millisecond);
                else
                {
                    _state.TimeNBBO = true;
                    _state.sixEntryChecks++;
                    return;
                }
                var reference_Quote = _state.probeSide == 1 ? _state.Quotes.FirstOrDefault(x => x.Bid != _state.last_bid):
                    _state.Quotes.FirstOrDefault(x => x.Ask != _state.last_Ask) ;

                if(reference_Quote == null)
                {

                    _state.TimeNBBO = true;
                    _state.sixEntryChecks++;

                    return;
                }

                
                if ( (reference_Quote.QuoteTime.Millisecond-_state.last_quote.QuoteTime.Millisecond) < RebateTrader.minTmNBBOStable)
                    
                {
                    _state.wave = 0;
                    _state.isOn = false;
                    _state.TimeNBBO = false;
                    _state.sixEntryChecks++;

                    return;
                }

                
            }
            #endregion

            _state.TimeNBBO = true;
            _state.sixEntryChecks++;
          
        }
        private void CheckDelta(individual_security_params _state,Option op)
        {
            
            if ((DateTime.Now - _state.DeltaUpdateTime).TotalMilliseconds >= 900000)
            {
               
                _state.update_IV_and_greeks(op, security);
               
                _state.DeltaUpdateTime = DateTime.Now;
            }
                
            _state.DeltaCheck = Math.Abs(_state.option_greeks.Delta) >= RebateTrader.minSTKdelta && Math.Abs(_state.option_greeks.Delta) <= RebateTrader.maxSTKdelta;
            
            _state.sixEntryChecks ++;
        }
        private void CheckNBBOSpread(individual_security_params _state,Option op)
        {
            #region check whether NBBO with within max and min limits.. different for penny and nickel stocks

            double min_tick = !(op.Underlying.Symbol == "SPY" || op.Underlying.Symbol == "IWM" || op.Underlying.Symbol == "QQQ") ?
              (_state.probeSide == 1 ? (_state.last_bid >= 3 ? (op.MinimumTick == 0.01 ? 0.05 : 0.1) : (op.MinimumTick == 0.01 ? 0.01 : 0.05)) : (_state.last_Ask <= 3 ? (op.MinimumTick == 0.01 ? 0.01 : 0.05) : (op.MinimumTick == 0.01 ? 0.05 : 0.1)))
               : 0.01;
            
            if (op.MinimumTick == 0.01)
            {
                
                    _state.NBBOSpread = ((int)((_state.last_Ask - _state.last_bid) / min_tick) >= RebateTrader.minTickWIDTH_for_penny)
                    && ((int)((_state.last_Ask - _state.last_bid) / min_tick) <= RebateTrader.maxTickWIDTH_for_penny);
                    _state.sixEntryChecks++;
    
                    _state.sixEntryChecks++;

                    return;
                
            }
            
            else if (op.MinimumTick == 0.05)
            {
                
                    _state.NBBOSpread = (((_state.last_Ask - _state.last_bid) / min_tick) >= RebateTrader.minTickWIDTH_for_nickel)
                    && (((_state.last_Ask - _state.last_bid) / min_tick) <= RebateTrader.maxTickWIDTH_for_nickel);

                    _state.sixEntryChecks++;
                
                    return;
                
            }
            else
            {
                
                _state.NBBOSpread = false;
                _state.sixEntryChecks++;

                return;
            }

            
            #endregion
        }
        private void UnderlySpread(individual_security_params _state, Option op)
        {

            #region check whether undeerlying stock spread is within limits

           
                _state.UnderlySpread= (int)((Stock_Last_Ask - Stock_Last_Bid) / security.MinimumTick) <= RebateTrader.maxStockNBBOwidth;
                _state.sixEntryChecks++;
               
                return;
            
                      #endregion

        }

        #endregion

        #region ------------- Execution & params---------------------
        public void check_trade(individual_security_params _state)
        {
            
         
            _state.trade_prices.RemoveAll(x => (_state.trade_prices.First().trade_time - x.trade_time).TotalMilliseconds > RebateTrader.maxPRITNSTmGap);
           
            if (!_state.isOn && !_state.isToxicStrikes && !isToxicSymbol && _state.trade_prices.Count() >= RebateTrader.minPRINTS && _state.quote_updated) 
            {
                _state.isOn = true;
                if (!RebateTrader.isPassiveTrading)
                {
                    new Thread(() => { Initial_condition_and_order(_state); _state.isOn = false; }).Start() ;
                    
                }
                else if (RebateTrader.isPassiveandTrading < RebateTrader.MaxOrdersPassive)
                {

                    RebateTrader.isPassiveandTrading++;
                    new Thread(() => { Initial_condition_and_order(_state); _state.isOn = false; }).Start() ;
                    

                }

            }

        }

        private void RemoveQuotes(individual_security_params _state)
        {
            //_state.Quotes.RemoveAll(x => (_state.Quotes.First().QuoteTime - x.QuoteTime) > 100);
        }

        private void sendWave(Option op, individual_security_params _state)//determiines order size from size vaurbale in individual security params class and calls on sendWaveorder function to client
        {

            int sz = 0;
            switch (_state.wave)
            {
                case 1:
                    sz = RebateTrader.Wave1;
                    break;
                case 2:
                    sz = RebateTrader.Wave2;
                    break;
                case 3:
                    sz = RebateTrader.Wave3;
                    break;
                case 4:
                    sz = RebateTrader.Wave4;
                    break;
            }



            if (sz > 0)

                sendWaveOrder(op, _state, sz);
            
        }
       
        private void sendWaveOrder(Option op, individual_security_params _state, int ordSZ)
        {
            
            if (!(CheckRisk(op, ordSZ, _state.wave) && CheckOrder(op,ordSZ)))
            
                return; // this will check entry criteria again and see if everything is okay.. and recheck risk after
            
            
            double min_tick = !(op.Underlying.Symbol=="SPY" || op.Underlying.Symbol == "IWM"|| op.Underlying.Symbol == "QQQ")?
               ( _state.probeSide ==1? (_state.last_bid >= 3 ? (op.MinimumTick == 0.01 ? 0.05 : 0.1) : (op.MinimumTick == 0.01 ? 0.01 : 0.05)) : (_state.last_Ask <= 3 ? (op.MinimumTick == 0.01 ? 0.01 : 0.05) : (op.MinimumTick == 0.01 ? 0.05 : 0.1)))
                : 0.01;
            Order order = null;
           
            if (_state.probeSide == 1)
            {
                  
               // order = OrderFactory.BuyLimit((op as Security), op.MinimumTick==0.01?"BATS":"C2", _state.last_bid + (_state.entry_at_NBBO ? 0 : 1 * min_tick), ordSZ, "REBATE-OPENING");
                order = OrderFactory.BuyLimit((op as Security), "DEMO", _state.last_bid + (_state.entry_at_NBBO ? 0 : 1 * min_tick), ordSZ, "REBATE-OPENING");
                _state.entryBid = _state.last_bid;//seeting these variable for reference while selling... security bids and asks may change later
              
                _state.closingflashOrder = OrderFactory.SellLimit((op as Security), order.Route, order.Price + min_tick, 0, "FLASH-ORDER");
            }
            if (_state.probeSide == -1)
            {
    
               //    order = OrderFactory.SellLimit((op as Security), min_tick >= 0.05 ? "C2" : "BATS", _state.last_Ask - (_state.entry_at_NBBO ? 0 : 1 * min_tick), ordSZ, "REBATE-OPENING");
                order = OrderFactory.SellLimit((op as Security), "DEMO", _state.last_Ask - (_state.entry_at_NBBO ? 0 : 1 * min_tick), ordSZ, "REBATE-OPENING");

                _state.entryAsk = _state.last_Ask;
                _state.closingflashOrder = OrderFactory.BuyLimit((op as Security), order.Route, order.Price - min_tick, 0, "FLASH-ORDER");
            }
            
            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");
            
            _state.closingflashOrder.Account = order.Account;
            _state.isOpnWaveComplete = false;
            _state.isPartialFill = false;


            System.Timers.Timer cancel_timer_opening = new System.Timers.Timer();
            cancel_timer_opening.Interval = RebateTrader.flash_order_time;

            cancel_timer_opening.AutoReset = false;
          
            order.OrderUpdated += delegate (IOrder or, IOrderUpdate update)
            {

                switch (or.OrdStatus)
                {
                    case OrdStatus.New:  //open new
                      
                        cancel_timer_opening.Enabled = true;
                        RebateTrader.total_orders++;
                        orders_per_symbol++;
                        break;

                    case OrdStatus.Rejected: //rejected
                        cancel_timer_opening.Stop();
                        _state.wave = 0;
                        _state.hold_opening_thread.Set();
                        break;

                    case OrdStatus.PartiallyFilled:  //partially filled
                        _state.isPartialFill = true;
                        _state.posSZ += Math.Abs(or.LastQty);
                        _state.closingflashOrder.Qty += Math.Abs(or.LastQty);
                        _state.fill_price = or.AvgPx;
                        break;

                    case OrdStatus.Filled: //filled
                        _state.isOpnWaveComplete = true;
                        _state.isPartialFill = false;
                        _state.posSZ += Math.Abs(or.LastQty);
                        _state.closingflashOrder.Qty += Math.Abs(or.Qty);
                        _state.hold_opening_thread.Set();
                        _state.fill_price = or.AvgPx;
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        break;

                    case OrdStatus.Canceled: //caneled
                        cancel_timer_opening.Stop();
                        _state.hold_opening_thread.Set();
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        if(!_state.isPartialFill)
                        _state.wave = 0;
                        break;

                    default:

                        break;

                }

            };
            client.SendOrder(order); //sends order to client
            
            cancel_timer_opening.Elapsed += delegate
            {
                if (order.OrdStatus != OrdStatus.Filled)
                    client.CancelOrder(order);

            };

            System.Timers.Timer flash_timer = new System.Timers.Timer();

            flash_timer.Interval = RebateTrader.OneTickUpTime;

            flash_timer.AutoReset = false;

            _state.closingflashOrder.OrderUpdated += delegate (IOrder or, IOrderUpdate update)
            {


                switch (or.OrdStatus)
                {
                    case OrdStatus.New:  //open new
                        flash_timer.Enabled = true;
                        RebateTrader.total_orders++;
                        orders_per_symbol++;
                        break;
                    case OrdStatus.Rejected: //rejected
                        _state.hold_flash_thread.Set();
                        _state.closing_thread.Set();
                        break;
                    case OrdStatus.PartiallyFilled:  //partially filled
                        _state.posSZ -= Math.Abs(or.LastQty);
                        break;
                    case OrdStatus.Filled: //filled
                        flash_timer.Stop();
                        _state.posSZ -= Math.Abs(or.LastQty);
                        _state.hold_flash_thread.Set();
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        break;
                    case OrdStatus.Canceled: //caneled
                        _state.hold_flash_thread.Set();
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        break;
                    default:
                        break;
                }

            };

            _state.hold_opening_thread.WaitOne();
            _state.hold_opening_thread.Reset();

            if (_state.isPartialFill || _state.isOpnWaveComplete)
            {
                client.SendOrder(_state.closingflashOrder);
                flash_timer.Elapsed += delegate
                {

                    if (_state.closingflashOrder.OrdStatus != OrdStatus.Filled)
                        client.CancelOrder(_state.closingflashOrder);

                };

                //comes here afterflashing order

                closing_wave(_state._option, _state);
            }
            //flashIn(_state);

        } 

        private void flashIn(individual_security_params _state)
        {

            
            
        }

        private void close_one_tick_lower(Option option, individual_security_params _state, int CritSZ)
        {
            //_state.fill_price = client.Portfolio.GetPositions().FirstOrDefault(x => x.Security.Symbol == option.Symbol && x.Account== client.Accounts.FirstOrDefault(y => y.RoutingID == "AVT4")).AvgCost;
            
            #region send order
          //  double prc;
            Order order = null;

            double min_tick = !(option.Underlying.Symbol == "SPY" || option.Underlying.Symbol == "IWM" || option.Underlying.Symbol == "QQQ") ?
                 (_state.probeSide == 1 ? (_state.fill_price >= 3 ? (option.MinimumTick == 0.01 ? 0.05 : 0.1) : (option.MinimumTick == 0.01 ? 0.01 : 0.05)) : (_state.fill_price <= 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1))) 
                 : 0.01;
            
            if (_state.probeSide == 1)
            {
                if (_state.last_bid == _state.fill_price + min_tick)
                {
                   
                // order = OrderFactory.SellLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", _state.last_bid + min_tick, _state.posSZ, "REBATE-CLOSING");
                    order = OrderFactory.SellLimit((option as Security), "DEMO", _state.last_bid + min_tick, _state.posSZ, "REBATE-CLOSING");

                }
                else if (_state.last_bid > _state.fill_price + min_tick)
                {
                    
                 //   order = OrderFactory.SellLimit((option as Security), "CBOE", _state.last_bid, _state.posSZ, "REBATE-CLOSING");
                   order = OrderFactory.SellLimit((option as Security), "DEMO", _state.last_bid, _state.posSZ, "REBATE-CLOSING");

                }
                else
                {

                    
                  //   order = OrderFactory.SellLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", _state.fill_price, _state.posSZ, "REBATE-CLOSING");
                    order = OrderFactory.SellLimit((option as Security), "DEMO", _state.fill_price, _state.posSZ, "REBATE-CLOSING");

                }
            }

            if (_state.probeSide == -1)
            {
                if (_state.last_Ask == _state.fill_price - min_tick)
                {
                    
                  //    order = OrderFactory.BuyLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", _state.last_Ask - min_tick, _state.posSZ, "REBATE-CLOSING");
                    order = OrderFactory.BuyLimit((option as Security), "DEMO", _state.last_Ask - min_tick, _state.posSZ, "REBATE-CLOSING");

                }
                else if (_state.last_Ask < _state.fill_price - min_tick)
                {
                    
                  //     order = OrderFactory.BuyLimit((option as Security), "CBOE", _state.last_Ask, _state.posSZ, "REBATE-CLOSING");
                   order = OrderFactory.BuyLimit((option as Security), "DEMO", _state.last_Ask, _state.posSZ, "REBATE-CLOSING");

                }
                else
                {
                    
                 //       order = OrderFactory.BuyLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", _state.fill_price, _state.posSZ, "REBATE-CLOSING");
                    order = OrderFactory.BuyLimit((option as Security), "DEMO", _state.fill_price, _state.posSZ, "REBATE-CLOSING");

                }
            }

            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");


           

            #endregion

            bool isOrdcomplete = false;
            System.Timers.Timer update_timer = new System.Timers.Timer();

            update_timer.Interval = RebateTrader.flash_order_time;

            order.OrderUpdated += delegate (IOrder or, IOrderUpdate update)
            {
                switch (or.OrdStatus)
                {
                    case OrdStatus.New:  //open new
                        update_timer.Enabled = true;
                        RebateTrader.total_orders++;
                        orders_per_symbol++;
                        break;
                    case OrdStatus.PartiallyFilled:  //partially filled
                        _state.posSZ -= Math.Abs(or.LastQty);
                        break;
                    case OrdStatus.Rejected: //rejected
                        if (_state.posSZ > 0) //this is for incase it gets a fill and the cancel order is sent anyway... the fill update will come in first assigning posSZ=0
                        {
                            if (option.MinimumTick == 0.01)
                            {
                                if (RebateTrader.isScratchPennies)
                                
                                    _state.isScratching = true;

                                
                                else
                                
                                    _state.isHedging = true;

                                
                            }
                            if (option.MinimumTick == 0.05)
                            {
                                if (RebateTrader.isScratchNickels)
                                

                                    _state.isScratching = true;

                                
                                else
                                
                                    _state.isHedging = true;

                                
                            }
                        }
                        _state.hold_flash_thread.Set();
                        break;
                    case OrdStatus.Filled: //filled
                        _state.posSZ -= Math.Abs(or.LastQty); 
                        isOrdcomplete = true;
                        update_timer.Stop();
                        update_timer.Dispose();
                        _state.hold_flash_thread.Set();
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        _state.closing_thread.Set();
                        break;

                    case OrdStatus.Canceled: //canceled
                        update_timer.Stop();
                        update_timer.Dispose();
                        isOrdcomplete = true;
                        _state.hold_flash_thread.Set();
                        RebateTrader.total_orders--;
                        orders_per_symbol--;
                        Console.WriteLine("cancelled order because of "+(_state.isScratching?"Scratching": (_state.isHedging ? "Hedging" :"attempted to close 500 times"))+" for "+option.Symbol);
                        break;
                    case OrdStatus.Replaced:
                        update_timer.Enabled = true;
                        break;

                    default:
                        break;

                }
               
            };

            _state.hold_flash_thread.WaitOne(); //used in close one tick down... they cannot practically overlap
            _state.hold_flash_thread.Reset();

            if (_state.posSZ > 0)
            {
                order.Qty = _state.posSZ;

                client.SendOrder(order); //sends order to client
                update_timer.AutoReset = false;
                update_timer.Elapsed += delegate
                {

                    _state.closing_attempt++;

                    if (_state.closing_attempt >= 500)
                    {

                        if (order.OrdStatus != OrdStatus.Filled && !_state.isHedging && !_state.isScratching)
                            client.CancelOrder(order);

                        _state.isToxicStrikes = true;

                    }
                    else if (order.OrdStatus != OrdStatus.Filled)
                    {

                        double prc_up = 0;
                        string route;

                    #region replace order price


                    if (_state.probeSide == 1)
                        {
                            if (_state.last_bid == _state.fill_price + min_tick)
                            {
                                prc_up = _state.last_bid + min_tick; // no change as we need to take liquidity
                                                                     //  route = option.MinimumTick == 0.05 ? "C2" : "BATS";

                        }
                            else if (_state.last_bid > _state.fill_price + min_tick)
                            {
                                prc_up = _state.last_bid;
                            //   route = "CBOE";
                        }
                            else
                            {

                                prc_up = _state.fill_price;
                            //    route = option.MinimumTick == 0.05 ? "C2" : "BATS";

                        }
                        }

                        if (_state.probeSide == -1)
                        {
                            if (_state.last_Ask == _state.fill_price - min_tick)
                            {
                                prc_up = _state.last_Ask - min_tick;
                            //    route = option.MinimumTick == 0.05 ? "C2" : "BATS";
                        }
                            else if (_state.last_Ask < _state.fill_price - min_tick)
                            {
                                prc_up = _state.last_Ask;
                            //  route = "CBOE";
                        }
                            else
                            {
                                prc_up = _state.fill_price;
                            //route = option.MinimumTick == 0.05 ? "C2" : "BATS";

                        }
                        }

                    #endregion

                    if ((order.Price != prc_up) && !_state.isHedging && !_state.isScratching) //|| order.Route != route
                        client.ReplaceOrder(order, _state.posSZ, prc_up);
                        else
                            update_timer.Enabled = true;
                    //need to determine how to change route

                }



                };

                do
                {

                    if (_state.closing_attempt > 1)
                    {
                        #region close if NBBO falls below critical size
                        if ((_state.probeSide == 1 ? _state.bid_size <= CritSZ : _state.ask_size <= CritSZ) || (min_tick == 0.01 && (_state.probeSide == 1 ? _state.last_bid <= (_state.entryBid - 0.01 * RebateTrader.ScratchingTicksforPennies) : _state.last_Ask >= (_state.entryAsk + 0.01 * RebateTrader.ScratchingTicksforPennies))) ||
                           (min_tick == 0.05 && (_state.probeSide == 1 ? _state.last_bid <= (_state.entryBid - 0.05 * RebateTrader.ScratchingTicksforNickels) : _state.last_Ask >= (_state.entryAsk + 0.05) * RebateTrader.ScratchingTicksforNickels))
                           || (min_tick == 0.1 && (_state.probeSide == 1 ? _state.last_bid <= (_state.entryBid - 0.1 * RebateTrader.ScratchingTicksforNickels) : _state.last_Ask >= (_state.entryAsk + 0.1) * RebateTrader.ScratchingTicksforNickels))
                                )
                        {
                            _state.isToxicStrikes = true;

                            if (option.MinimumTick == 0.01)
                            {
                                if (RebateTrader.isScratchPennies)
                                {
                                    _state.isScratching = true;

                                    if (order.OrdStatus != OrdStatus.Filled)
                                        client.CancelOrder(order);

                                    break;

                                }
                                else
                                {
                                    _state.isHedging = true;

                                    if (order.OrdStatus != OrdStatus.Filled)
                                        client.CancelOrder(order);


                                    break;


                                }
                            }
                            else
                            {
                                if (RebateTrader.isScratchNickels)
                                {

                                    _state.isScratching = true;

                                    if (order.OrdStatus != OrdStatus.Filled)
                                        client.CancelOrder(order);


                                    break;


                                }
                                else
                                {
                                    _state.isHedging = true;

                                    if (order.OrdStatus != OrdStatus.Filled)
                                        client.CancelOrder(order);

                                    break;
                                }
                            }

                        }

                        #endregion
                    }
                    else
                        Thread.Sleep(100);
                    

                } while (!isOrdcomplete);

                _state.hold_flash_thread.WaitOne();  //used in flash... they cannot practically overlap
                _state.hold_flash_thread.Reset();


                //order did not complete
                if (_state.isScratching)
                
                    Scratch(option, _state);
                
                else if (_state.isHedging)
                
                    StockHedge(option, _state);
                
            }
        }

        private void Scratch(Option option, individual_security_params _state)
        {
            System.Timers.Timer cancel_timer = new System.Timers.Timer();
            cancel_timer.Interval = RebateTrader.flash_order_time;

            _state.wave = 0;
            Order order = null;
            
            double min_tick = !((option as Option).Underlying.Symbol == "SPY" || (option as Option).Underlying.Symbol == "IWM" || (option as Option).Underlying.Symbol == "QQQ") ?
               (_state.probeSide == 1 ? (_state.fill_price > 3 ? (option.MinimumTick == 0.01 ? 0.05 : 0.1) : (option.MinimumTick == 0.01 ? 0.01 : 0.05)) : (_state.fill_price < 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1))) :
               0.01;
            if (_state.probeSide == 1)
            
             
                // order = OrderFactory.SellLimit(option,price<=_state.last_bid? "CBOE":"BATS", Math.Max(_state.last_bid, (_state.fill_price - min_tick)),_state.posSZ, "SCRATCHING");
                order = OrderFactory.SellLimit(option, "DEMO", Math.Max(_state.last_bid, (_state.fill_price- min_tick)), _state.posSZ, "SCRATCHING");
            
            else
            
             
                //       order= OrderFactory.BuyLimit(option, price >=_state.last_Ask ? "CBOE" : "BATS",Math.Min(_state.last_Ask, (_state.entryAsk + min_tick)), _state.posSZ, "SCRATCHING");
                order = OrderFactory.BuyLimit(option, "DEMO", Math.Min(_state.last_Ask, (_state.fill_price + min_tick)), _state.posSZ, "SCRATCHING");


            

            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");
            


            order.OrderUpdated += delegate (IOrder or, IOrderUpdate update)
            {


                switch (or.OrdStatus)
                {
                    case OrdStatus.New:  //open new
                        cancel_timer.Enabled = true;
                        break;
                    case OrdStatus.PartiallyFilled:  //partially filled
                        _state.posSZ -= Math.Abs(or.LastQty);
                        break;
                    case OrdStatus.Rejected: //rejected
                        cancel_timer.Stop();
                        _state.hold_flash_thread.Set();
                        break;
                    case OrdStatus.Filled: //filled
                        _state.isScratching = false;
                        _state.posSZ -= Math.Abs(or.LastQty);
                        cancel_timer.Stop();
                        _state.hold_flash_thread.Set();
                        _state.closing_thread.Set();
                        break;

                    case OrdStatus.Canceled: //canceled
                        _state.hold_flash_thread.Set();
                        break;
                    default:
                        break;
                }

            };
            client.SendOrder(order);
          
            cancel_timer.AutoReset = false;
            cancel_timer.Elapsed += delegate
            {
                    
                        if (order.OrdStatus != OrdStatus.Filled)
                            client.CancelOrder(order);
                    
                    
            };

            _state.hold_flash_thread.WaitOne();
            _state.hold_flash_thread.Reset();
        }

        private void StockHedge(Option option, individual_security_params _state)
        {
            
            _state.wave = 0;
            
            #region update greeks

            _state.update_IV_and_greeks(option, security);

            _state.DeltaUpdateTime = DateTime.Now;

            #endregion

            Order order = null;
            //int size= (int)((Math.Abs((_state.option_greeks.Delta) * (double)100)) * _state.posSZ);
            int size = (int)(Math.Round((Math.Abs(_state.option_greeks.Delta) *100) * _state.posSZ,2,MidpointRounding.AwayFromZero));
            
            if (size == 0)
            {
                Console.WriteLine("Unable to hedge stock");
                Console.WriteLine("Delta :"+ _state.option_greeks.Delta);
                Console.WriteLine("option inventory:"+ _state.posSZ);
                
                return;
            }
            ////var pos = client.Portfolio.GetPositions().FirstOrDefault(x => x.Security.Symbol == underlying.Symbol);

            ////if (pos == null)
            //    total_stock_inventory = 0;
            //else
            //    total_stock_inventory = pos.NetQty;

            do
            {
                
                if (option.PutCall == PutCall.Call)
                {
                    #region call 
                    if (_state.probeSide == 1)
                    {
                        if (total_stock_inventory != 0)
                        {
                            if (total_stock_inventory < 0)
                            {


                                //  order = OrderFactory.SellShortLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask- Stock_Last_Bid) <= 0.02) ? 0.02 :  (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                                , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");
                                order = OrderFactory.SellShortLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");


                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");
                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                            else if (total_stock_inventory - (size - _state.stock_inventory_for_strike) < 0)
                            {

                                //   order = OrderFactory.SellLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                                   , total_stock_inventory, "HEDGE-ORDER");
                                order = OrderFactory.SellLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                             , total_stock_inventory, "HEDGE-ORDER");


                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                            else
                            {

                                //order = OrderFactory.SellLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                          , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");


                                order = OrderFactory.SellLimit(security, "DEMO",
                                Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                        , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");


                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                        }
                        else
                        {

                            //   order = OrderFactory.SellShortLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                            //                                , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");
                            order = OrderFactory.SellShortLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                         , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");


                            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                            
                            Stock_order(_state, order, order.Qty, true, false);
                        }
                    } else
                    {
                        //order = OrderFactory.BuyLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross),
                        //size - _state.stock_inventory_for_strike, "HEDGE-ORDER"); //-1 on size because put option delta is negative
                        order = OrderFactory.BuyLimit(security, "DEMO", Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross),
                                                                               size - _state.stock_inventory_for_strike, "HEDGE-ORDER"); //-1 on size because put option delta is negative


                        order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                        
                        Stock_order(_state, order, order.Qty, true, true);
                        //  add stock inventory before leaving function
                    }
                    #endregion
                }
                else
                {
                    #region put
                    if (_state.probeSide == 1)
                    {

                        // order = OrderFactory.BuyLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 :  (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross),
                        //size - _state.stock_inventory_for_strike, "HEDGE-ORDER"); //-1 on size because put option delta is 

                        order = OrderFactory.BuyLimit(security, "DEMO", Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross),
                         size - _state.stock_inventory_for_strike, "HEDGE-ORDER"); //-1 on size because put option delta is 


                        order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                        
                        Stock_order(_state, order, order.Qty, true, true);
                    }
                    else
                    {
                        if (total_stock_inventory != 0)
                        {
                            if (total_stock_inventory <= 0)
                            {

                                //          order = OrderFactory.SellShortLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                                         , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");

                                order = OrderFactory.SellShortLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross) * (1 - RebateTrader.stock_cross)
                                                                   , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");
                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                            else if (total_stock_inventory - (size - _state.stock_inventory_for_strike) < 0)
                            {
                                //order = OrderFactory.SellLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                               , total_stock_inventory, "HEDGE-ORDER");

                                order = OrderFactory.SellLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                               , total_stock_inventory, "HEDGE-ORDER");


                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                            else
                            {

                                //order = OrderFactory.SellLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                //                              , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");

                                order = OrderFactory.SellLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                                  , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");



                                order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");


                                
                                Stock_order(_state, order, order.Qty, true, false);
                            }
                        }
                        else
                        {

                            //  order = OrderFactory.SellShortLimit(security, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                            //                                 , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");

                            order = OrderFactory.SellShortLimit(security, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : (Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)
                                                          , size - _state.stock_inventory_for_strike, "HEDGE-ORDER");
                            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                            
                            Stock_order(_state, order, order.Qty, true, false);
                        }
                    }
                    #endregion
                }

            } while (_state.stock_inventory_for_strike != size && _state.Stock_opening_reject_count <= 3);


            _state.Stock_opening_reject_count = 0;
            if(_state.stock_inventory_for_strike >0)
            _state.isStockHedged = true;//-------------------------------------------wait untill filled
                                        //_state.isToxicStrikes = true;


        }

        private void ClosingStrategy(individual_security_params _state, Option option)
        {


            while (orders_per_symbol + 1 > RebateTrader.maxOrdr_symbol || RebateTrader.total_orders + 1 > RebateTrader.maxTotalOrder)
                Thread.Sleep(500);
            

            //check wether even one more contract will break this criteria for max order out per symbol and total
            int size = 0;
            Order or = null;

            if (_state.probeSide == 1)
            {

                do
                {

                    double min_tick = !(option.Underlying.Symbol == "SPY" || option.Underlying.Symbol == "IWM" || option.Underlying.Symbol == "QQQ") ?
                 (_state.last_Ask <= 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1))
                 : 0.01;
                    int jumps = 1;
                    double price = _state.last_Ask - jumps * min_tick > _state.last_bid ? _state.last_Ask - jumps * min_tick : _state.last_Ask;

                    #region hit prices above theo and on theo

                    size = (_state.posSZ >= RebateTrader.maxOrderSZ) ? RebateTrader.maxOrderSZ : _state.posSZ;
                  //  or = OrderFactory.SellLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", price, size, "CLOSING-STRATEGY");
                    or = OrderFactory.SellLimit((option as Security), "DEMO", price, size, "CLOSING-STRATEGY");
                    or.Account = or.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                    System.Timers.Timer cancel_timer = new System.Timers.Timer();
                    cancel_timer.Interval = RebateTrader.flash_order_time;
                    ManualResetEvent cancel_order = new ManualResetEvent(false);
                    or.OrderUpdated += delegate (IOrder o, IOrderUpdate update)
                    {
                        switch (o.OrdStatus)
                        {
                            case OrdStatus.Replaced:  //open new
                                cancel_timer.Enabled = true;
                                break;

                            case OrdStatus.New:  //open new
                                cancel_timer.Enabled = true;
                                cancel_order.Reset();
                                break;
                            case OrdStatus.PartiallyFilled:  //partially filled
                                _state.posSZ -= o.LastQty;
                                if (_state.isStockHedged)
                                {

                                    _state.stock_position_to_close += _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike,(int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));
                                    _state.stock_inventory_for_strike -= _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));// see Stock close function in order updator to see why stock inventory accouting is done here

                                    if (!_state.isStockClosing && _state.stock_position_to_close != 0)
                                    {
                                        _state.isStockClosing = true;
                                        new Thread(() => Stock_close(option, _state)).Start(); 
                                        
                                    }
                                    else
                                        _state.StockClose_waithandle.Set();
                                }
                             
                                break;
                            case OrdStatus.Rejected: //rejected
                                Console.WriteLine("Closing Starategy order rejected at " + option.Symbol + " New route:");
                                or.Route = Console.ReadLine();
                                break;
                            case OrdStatus.Filled: //filled

                                cancel_timer.Stop();
                                cancel_timer.Dispose();
                                _state.posSZ -= o.LastQty;
                                if (_state.isStockHedged)
                                {
                                    _state.stock_position_to_close += _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));
                                    _state.stock_inventory_for_strike -= _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));// see Stock close function in order updator to see why stock inventory accouting is done here

                                    if (!_state.isStockClosing && _state.stock_position_to_close != 0)
                                    {
                                        _state.isStockClosing = true;
                                        new Thread(() => Stock_close(option, _state)).Start();
                                        
                                    }
                                    else
                                        _state.StockClose_waithandle.Set();
                                }
                                else
                                    _state.closing_thread.Set();

                                _state.hold_flash_thread.Set();
                                break;

                            case OrdStatus.Canceled:  //open new
                                cancel_order.Set();
                                break;
                            default:
                                break;
                        }

                    };
                    client.SendOrder(or);

                    cancel_timer.AutoReset = false;
                    cancel_timer.Elapsed += delegate
                    {

                        if (or.OrdStatus != OrdStatus.Canceled)
                            client.CancelOrder(or);
                        cancel_order.WaitOne();
                        Thread.Sleep(RebateTrader.Rest_Close_time);
                        // no need to reset .. it resets in orderhandler .. code will stall if you reset here incase order is made to wait because there is no edge
                        bool rest = false;
                        min_tick = !(option.Underlying.Symbol == "SPY" || option.Underlying.Symbol == "IWM" || option.Underlying.Symbol == "QQQ") ?
                (_state.last_Ask <= 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1)) : 0.01;
                        if (or.Price > _state.last_Ask)
                        {
                            //restart
                            jumps = 1;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else if ((_state.last_Ask - ((_state.last_Ask - _state.last_bid) / 2)) >= (_state.last_Ask - (jumps + 1) * min_tick))
                        {

                            //restart
                            jumps = 1;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else if ((_state.last_Ask - ((_state.last_Ask - _state.last_bid) / 2)) >= (_state.last_Ask - (jumps + 2) * min_tick))
                        {

                            cancel_timer.Interval = RebateTrader.finalPriceTimer;
                            // next price is on theo

                            jumps++;
                        }
                        //else if (_state.last_Ask - _state.last_bid <= min_tick)
                        //{
                        //    //restart
                        //    jumps = 1;
                        //    cancel_timer.Interval = RebateTrader.flash_order_time;
                        //    rest = true;
                        //}
                        else
                        {

                            jumps++;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }

                        price = jumps == 1 ? (_state.last_Ask - jumps * min_tick > _state.last_bid ?
                        _state.last_Ask - jumps * min_tick : _state.last_Ask) : _state.last_Ask - jumps * min_tick;
                        size = (_state.posSZ >= RebateTrader.maxOrderSZ) ? RebateTrader.maxOrderSZ : _state.posSZ;

                        or.Qty = size;
                        or.Price = price;

                        if (!rest)
                        {
                            client.SendOrder(or);
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else
                        {
                            cancel_timer.Interval = 200;
                            cancel_timer.Enabled = true;
                        }
                    };

                    _state.hold_flash_thread.WaitOne();
                    _state.hold_flash_thread.Reset();

                    #endregion

                    if (_state.posSZ > 0)
                        Thread.Sleep(RebateTrader.finalPriceTimer); //sleep for long time

                    //try to close again is not closed already
                } while (_state.posSZ > 0);//repeat if position did not close

            }
            else  //probeside is -1

            {
             
                do
                {
                    double min_tick = !(option.Underlying.Symbol == "SPY" || option.Underlying.Symbol == "IWM" || option.Underlying.Symbol == "QQQ") ?
                  (_state.last_bid <= 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1))
                  : 0.01;
                    int jumps = 1;
                    double price = _state.last_bid + jumps * min_tick < _state.last_Ask ? _state.last_bid + jumps * min_tick : _state.last_bid;

                    #region hit above theo and on theo

                    size = (_state.posSZ >= RebateTrader.maxOrderSZ) ? RebateTrader.maxOrderSZ : _state.posSZ;
                  //    or = OrderFactory.BuyLimit((option as Security), option.MinimumTick == 0.05 ? "C2" : "BATS", price, size, "CLOSING-STRATEGY");
                    or = OrderFactory.BuyLimit((option as Security), "DEMO", price, size, "CLOSING-STRATEGY");
                    or.Account = or.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");
                    System.Timers.Timer cancel_timer = new System.Timers.Timer();
                    cancel_timer.Interval = RebateTrader.flash_order_time;


                    ManualResetEvent cancel_order = new ManualResetEvent(false);
                    or.OrderUpdated += delegate (IOrder o, IOrderUpdate update)
                    {
                        switch (o.OrdStatus)
                        {
                            case OrdStatus.New:  //open new
                                cancel_timer.Enabled = true;
                                cancel_order.Reset();
                                break;
                            case OrdStatus.PartiallyFilled:  //partially filled
                                _state.posSZ -= o.LastQty;
                                if (_state.isStockHedged)
                                {
                                    _state.stock_position_to_close += _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));
                                    _state.stock_inventory_for_strike -= _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike,(int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));// see Stock close function in order updator to see why stock inventory accouting is done here

                                    if (!_state.isStockClosing && _state.stock_position_to_close != 0)
                                    {
                                        _state.isStockClosing = true;
                                        new Thread(() => Stock_close(option, _state)).Start();
                                        
                                    }
                                    else
                                        _state.StockClose_waithandle.Set();
                                }
                                
                                break;
                            case OrdStatus.Rejected: //rejected
                                Console.WriteLine("Closing Starategy order rejected at " + option.Symbol + " New route:");
                                or.Route = Console.ReadLine();
                                break;
                            case OrdStatus.Filled: //filled
                                
                                cancel_timer.Stop();
                                cancel_timer.Dispose();
                                _state.posSZ -= o.LastQty;
                                if (_state.isStockHedged)
                                {
                                    _state.stock_position_to_close += _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));
                                    _state.stock_inventory_for_strike -= _state.posSZ == 0 ? _state.stock_inventory_for_strike : Math.Min(_state.stock_inventory_for_strike, (int)Math.Abs(o.LastQty * _state.option_greeks.Delta * 100));// see Stock close function in order updator to see why stock inventory accouting is done here

                                    if (!_state.isStockClosing && _state.stock_position_to_close != 0)
                                    {
                                        _state.isStockClosing = true;
                                        new Thread(() => Stock_close(option, _state)).Start();

                                    }
                                    else
                                        _state.StockClose_waithandle.Set();

                                }
                                else
                                    _state.closing_thread.Set();
                                _state.hold_flash_thread.Set();



                                break;
                            case OrdStatus.Canceled:  //open new
                                cancel_order.Set();
                                break;
                            default:
                                break;
                        }



                    };
                    client.SendOrder(or);

                    cancel_timer.AutoReset = false;
                    cancel_timer.Elapsed += delegate
                    {
                        bool rest = false;
                        if (or.OrdStatus != OrdStatus.Canceled)
                            client.CancelOrder(or);
                        cancel_order.WaitOne();
                        Thread.Sleep(RebateTrader.Rest_Close_time);
                        // no need to reset .. gets reset in orderhandler
                        min_tick = !(option.Underlying.Symbol == "SPY" || option.Underlying.Symbol == "IWM" || option.Underlying.Symbol == "QQQ") ?
                 (_state.last_bid <= 3 ? (option.MinimumTick == 0.01 ? 0.01 : 0.05) : (option.MinimumTick == 0.01 ? 0.05 : 0.1))
                 : 0.01;
                        if (or.Price < _state.last_bid)
                        {

                            //restart
                            jumps = 1;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else if ((_state.last_bid + ((_state.last_Ask - _state.last_bid) / 2)) <= (_state.last_bid + (jumps + 1) * min_tick))
                        {
                            ;
                            //restart
                            jumps = 1;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else if ((_state.last_bid + ((_state.last_Ask - _state.last_bid) / 2)) <= (_state.last_bid - (jumps + 2) * min_tick))
                        {
                            cancel_timer.Interval = RebateTrader.finalPriceTimer;
                            // next price is on theo

                            jumps++;
                        }
                     //   else if (_state.last_Ask - _state.last_bid <= min_tick)
                       // {
                            //restart
                         //   jumps = 1;
                        //    cancel_timer.Interval = RebateTrader.flash_order_time;
                       //     rest = true;
                        //}
                        else
                        {

                            jumps++;
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                            
                        }

                        price = jumps == 1 ? (_state.last_bid + jumps * min_tick < _state.last_Ask ? _state.last_bid + jumps * min_tick : _state.last_bid) : _state.last_bid + jumps * min_tick;
                        size = (_state.posSZ >= RebateTrader.maxOrderSZ) ? RebateTrader.maxOrderSZ : _state.posSZ;
                        or.Price = price;
                        or.Qty = size;
                        if (!rest)
                        {
                            client.SendOrder(or);
                            cancel_timer.Interval = RebateTrader.flash_order_time;
                        }
                        else
                        {
                            cancel_timer.Interval = 200;
                            cancel_timer.Enabled = true;
                        }
                    };

                    _state.hold_flash_thread.WaitOne();
                    _state.hold_flash_thread.Reset();


                    #endregion

                    //try to close again is not closed already
                    if (_state.posSZ > 0)
                        Thread.Sleep(RebateTrader.finalPriceTimer); //sleep for long time
                } while (_state.posSZ > 0);//repeat if position did not close

            }
            
        }

        private void Stock_order(individual_security_params _state,Order order,int position_ratio,bool hedge_order,bool buy)
        {
            
           ManualResetEvent waithandle = new ManualResetEvent(false);
            ManualResetEvent Cancel_order = new ManualResetEvent(false);
            System.Timers.Timer cancel_stock_timer = new System.Timers.Timer();
                cancel_stock_timer.Interval = RebateTrader.flash_order_time;

            order.OrderUpdated += delegate (IOrder o, IOrderUpdate update)
                {
                    switch (o.OrdStatus)
                    {
                        case OrdStatus.New:  //open new
                            cancel_stock_timer.AutoReset = false;
                            cancel_stock_timer.Enabled = true;

                            break;
                        case OrdStatus.PartiallyFilled:  //partially filled
                            position_ratio -= Math.Abs(o.LastQty);
                            
                                _state.stock_inventory_for_strike += Math.Abs(o.LastQty);
                            
                            if (!buy)
                                total_stock_inventory -= Math.Abs(o.LastQty);
                            else
                                total_stock_inventory += Math.Abs(o.LastQty);
                            break;
                        case OrdStatus.Rejected: //rejected
                            _state.Stock_opening_reject_count++;
                            if (_state.Stock_opening_reject_count <= 3)
                            {
                               // order.Route = "EDGX";
                                if (buy)

                                    order.Price = Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));

                                else

                                    order.Price = Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));
                                
                                order.Qty = position_ratio;

                                client.SendOrder(order);
                            }
                            else
                            {
                                isToxicSymbol = true;
                                Console.WriteLine("Unable to Hedge With due to rejects for "+_state._option.Symbol);
                                waithandle.Set();
                            }
                            break;
                        case OrdStatus.Filled: //filled
                           
                                _state.stock_inventory_for_strike += Math.Abs(o.LastQty);

                            position_ratio -= Math.Abs(o.LastQty);
                            if (!buy)
                                total_stock_inventory -= Math.Abs(o.LastQty);
                            else
                                total_stock_inventory += Math.Abs(o.LastQty);

                            cancel_stock_timer.Stop();
                            cancel_stock_timer.Dispose();
                            waithandle.Set();

                            // ord_complete = true;
                            break;

                        case OrdStatus.Canceled: //canceled
                            if (cancel_stock_timer.Enabled)
                            {
                                //  order.Route = "EDGX";
                            }
                            Cancel_order.Set();
                            break;
                        case OrdStatus.Replaced:
                            cancel_stock_timer.Enabled = true;
                            break;

                        default:
                            break;
                    }

                };

            
            client.SendOrder(order);
                cancel_stock_timer.Elapsed += delegate
                {
                    if (order.OrdStatus != OrdStatus.Filled)
                    {
                        client.CancelOrder(order);
                        Cancel_order.WaitOne();
                        Cancel_order.Reset();
                        if (buy)

                            order.Price = Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));

                        else

                            order.Price = Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));

                        if (order.Route != "EDGX")
                        {
                          //  order.Route = ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS";
                        }
                        order.Qty = position_ratio;

                        client.SendOrder(order);

                    }



                };
           
            waithandle.WaitOne();
            waithandle.Reset();
            
        }

        private void Stock_close(Option option, individual_security_params _state)
        {

            Order order = null;
            
            ManualResetEvent Cancel_order = new ManualResetEvent(false);
            ManualResetEvent temp_waithandle = new ManualResetEvent(false);
            System.Timers.Timer cancel_stock_timer = new System.Timers.Timer();
            cancel_stock_timer.Interval = RebateTrader.flash_order_time;

            cancel_stock_timer.Elapsed += delegate
            {
                

                if (order.OrdStatus != OrdStatus.Filled && order.OrdStatus!=OrdStatus.Canceled && order.OrdStatus !=OrdStatus.Rejected)
                    client.CancelOrder(order);
                    Cancel_order.WaitOne();
                    Cancel_order.Reset();

                if (order.OrdStatus == OrdStatus.Canceled || (order.OrdStatus!=OrdStatus.New && order.OrdStatus !=OrdStatus.Filled))
                {
                    if ((option.PutCall == PutCall.Call && _state.probeSide == -1) || (option.PutCall == PutCall.Put && _state.probeSide == 1))
                    {
                        order.Price = Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));
                        if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                        {
                            order.Price = Stock_Last_Bid;
                           // order.Route = "BATSY";
                        }
                        else
                        {
                            order.Price = Stock_Last_Ask - Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                            //order.Route = "BATS";
                        }
                    }
                    else
                    {
                        order.Price = Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));
                        if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                        {
                            order.Price = Stock_Last_Ask;
                            //order.Route = "BATSY";
                        }
                        else
                        {
                            order.Price = Stock_Last_Bid + Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                            //order.Route = "BATS";
                        }
                    }

                    order.Qty = _state.stock_position_to_close;
                    if (order.Route != "EDGX")
                    {
                     //   order.Route = ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS";
                    }
                    client.SendOrder(order);
                }
                
                

               
               
            };

            do
            {
            
                _state.StockClose_waithandle.Reset();

                #region create order
                if ((option.PutCall==PutCall.Call && _state.probeSide== -1) || (option.PutCall == PutCall.Put && _state.probeSide == 1)) //stock is bought.. now sell
                {


                    if (total_stock_inventory != 0)
                    {
                        if (total_stock_inventory <= 0)
                        {

                            //   order = OrderFactory.SellShortLimit(option.Underlying, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS",
                            //       Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask-Stock_Last_Bid) * RebateTrader.stock_cross))
                            // , _state.stock_position_to_close, "STOCK-CLOSING");

                               order = OrderFactory.SellShortLimit(option.Underlying, "DEMO",
                                 Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                              , _state.stock_position_to_close, "STOCK-CLOSING");

                            if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                            {
                                order.Price = Stock_Last_Bid;
                               // order.Route = "BATSY";
                            }
                            else
                            {
                                order.Price= Stock_Last_Ask-Math.Round((Stock_Last_Ask-Stock_Last_Bid)*RebateTrader.stock_cross,2,MidpointRounding.AwayFromZero);
                                //order.Route = "BATS";
                            }

                           order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                            
                        }
                        else if (total_stock_inventory - _state.stock_position_to_close < 0)
                        {

                         //        order = OrderFactory.SellLimit(option.Underlying, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS",
                         //         Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                          //     , total_stock_inventory, "STOCK-CLOSING");

                            order = OrderFactory.SellLimit(option.Underlying, "DEMO", option.Underlying.Quote.Bid - (((option.Underlying.Quote.Ask - option.Underlying.Quote.Bid) <= 0.02) ? 0.02 : (-1 * ((option.Underlying.Quote.Ask - option.Underlying.Quote.Bid) * (1 - RebateTrader.stock_cross))))
                         , total_stock_inventory, "STOCK-CLOSING");

                            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                            if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                            {
                                order.Price = Stock_Last_Bid;
                               // order.Route = "BATSY";
                            }
                            else
                            {
                                order.Price = Stock_Last_Ask - Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                                //order.Route = "BATS";
                            }
                            
                        }
                        else
                        {

                                  order = OrderFactory.SellLimit(option.Underlying, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                            , _state.stock_position_to_close, "STOCK-CLOSING");
                         //      order = OrderFactory.SellLimit(option.Underlying, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS",
                           //       Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                             //    , _state.stock_position_to_close, "STOCK-CLOSING");
                            order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");


                            if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                            {
                                order.Price = Stock_Last_Bid;
                               // order.Route = "BATSY";
                            }
                            else
                            {
                                order.Price = Stock_Last_Ask - Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                                //order.Route = "BATS";
                            }
                            
                        }
                    }
                    else
                    {

                          // order = OrderFactory.SellShortLimit(option.Underlying, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS",
                          //          Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                          //  , _state.stock_position_to_close, "STOCK-CLOSING");

                        order = OrderFactory.SellShortLimit(option.Underlying, "DEMO", Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross))
                          , _state.stock_position_to_close, "STOCK-CLOSING");
                        order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                        if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                        {
                            order.Price = Stock_Last_Bid;
                            order.Route = "BATSY";
                        }
                        else
                        {
                            order.Price = Stock_Last_Ask - Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                            order.Route = "BATS";
                        }

                        
                    }
                }
                else
                {

                
                  //      order = OrderFactory.BuyLimit(option.Underlying, ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? "BATSY" : "BATS", Stock_Last_Bid + (((Stock_Last_Ask- Stock_Last_Bid) <= 0.02) ? 0.02 :  ((Stock_Last_Ask - Stock_Last_Bid) *  RebateTrader.stock_cross)),
                    //        _state.stock_position_to_close, "STOCK-CLOSING");

                    order = OrderFactory.BuyLimit(option.Underlying, "DEMO", Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross)),
                      _state.stock_position_to_close, "STOCK-CLOSING");
                    order.Account = order.Route == "DEMO" ? client.Accounts.FirstOrDefault(x => x.RoutingID == "D160418-1") : client.Accounts.FirstOrDefault(x => x.RoutingID == "AVT4");

                    if ((Stock_Last_Ask - Stock_Last_Bid) <= 0.02)
                    {
                        order.Price = Stock_Last_Ask;
                      //  order.Route = "BATSY";
                    }
                    else
                    {
                        order.Price = Stock_Last_Bid + Math.Round((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross, 2, MidpointRounding.AwayFromZero);
                        //order.Route = "BATS";
                    }

                    
                }
                #endregion

                client.SendOrder(order);

                order.OrderUpdated += delegate (IOrder o, IOrderUpdate update)
                {
                    switch (o.OrdStatus)
                    {
                        case OrdStatus.New:  //open new
                            cancel_stock_timer.AutoReset = false;
                            cancel_stock_timer.Enabled = true;

                            break;
                        case OrdStatus.PartiallyFilled:  //partially filled
                            _state.stock_position_to_close -= Math.Abs(o.LastQty);

                            if ((option.PutCall == PutCall.Call && _state.probeSide == -1) || (option.PutCall == PutCall.Put && _state.probeSide == 1))
                                total_stock_inventory -= Math.Abs(o.LastQty);

                            else
                                total_stock_inventory += Math.Abs(o.LastQty);


                            break;
                        case OrdStatus.Rejected: //rejected
                            _state.Stock_closing_reject_count++;
                            if (_state.Stock_closing_reject_count <= 3)
                            {
                                // order.Route = "EDGX";
                                if ((option.PutCall == PutCall.Call && _state.probeSide == -1) || (option.PutCall == PutCall.Put && _state.probeSide == 1))
                                {
                                    order.Price = Stock_Last_Ask - (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));

                                }
                                else
                                {
                                    order.Price = Stock_Last_Bid + (((Stock_Last_Ask - Stock_Last_Bid) <= 0.02) ? 0.02 : ((Stock_Last_Ask - Stock_Last_Bid) * RebateTrader.stock_cross));

                                }

                                order.Qty = _state.stock_position_to_close;
                                client.SendOrder(order);
                            }
                            else
                            {
                                isToxicSymbol = true;
                                Console.WriteLine("Unable to Close Stock due to rejects on " + option.Symbol);
                                temp_waithandle.Set();
                            }
                            break;
                        case OrdStatus.Filled: //filled
                            _state.stock_position_to_close -= Math.Abs(o.LastQty);
                            if ((option.PutCall == PutCall.Call && _state.probeSide == -1) || (option.PutCall == PutCall.Put && _state.probeSide == 1))
                                total_stock_inventory -= Math.Abs(o.LastQty);
                            else
                                total_stock_inventory += Math.Abs(o.LastQty);
                            cancel_stock_timer.Stop();
                            cancel_stock_timer.Dispose();

                            temp_waithandle.Set();
                            if(_state.stock_inventory_for_strike==0 && _state.stock_position_to_close==0)
                                _state.StockClose_waithandle.Set();
                            break;

                        case OrdStatus.Canceled: //canceled
                            if (cancel_stock_timer.Enabled)
                            {
                             //   order.Route = "EDGX";
                            }
                                Cancel_order.Set();
                                
                            break;
                        case OrdStatus.Replaced:
                            cancel_stock_timer.Enabled = true;
                            break;

                        default:
                            break;
                    }

                };
                
                temp_waithandle.Set();
                temp_waithandle.Reset();

                if (_state.Stock_closing_reject_count <= 3)
                    _state.StockClose_waithandle.WaitOne();
                

            } while ((_state.stock_position_to_close != 0 || _state.stock_inventory_for_strike!=0) && _state.Stock_closing_reject_count <=3);

            
            _state.Stock_closing_reject_count = 0;
            
            _state.closing_thread.Set();
            
        }
        #endregion

        private void start_trading(Option _option, individual_security_params _state)
        {
            #region main strategy
            
            _state.bw.WorkerReportsProgress = true;
            _state.bw.WorkerSupportsCancellation = true;
            
            
            client.AddTradeUpdateHandler((_option as Security), delegate (Security option_reference, Trade t)
            {
                
                  _state.last_trade = t.Last;
                _state.trade_prices.Insert(0, new trading_list_element(t.Last));
                if (!_state.bw.IsBusy)   //&& _state.quote_updated
                      _state.bw.RunWorkerAsync(t);
                
            });

            client.AddQuoteUpdateHandler((_option as Security), delegate (Security option_reference, Quote q)
            {

                _state.bid_size = q.BidSize;
                _state.ask_size = q.AskSize;
                _state.last_bid = q.Bid;
                _state.last_Ask = q.Ask;
               // _state.Quotes.Insert(0, q);
               // new Task(() => RemoveQuotes(_state)).Start();
               _state.quote_updated = true;
            });

            client.AddQuoteUpdateHandler(security, delegate (Security Stock, Quote q)
            {
              
                Stock_Last_Ask = q.Ask;
                Stock_Last_Bid = q.Bid;
                security.Quote.Bid = q.Bid;
                security.Quote.Ask = q.Ask;
            });
            #endregion
        }
    }
    
    class trading_list_element
    {
        public double trade_price;
        
        public DateTime trade_time;
        public trading_list_element(double price)
        {
            trade_time = DateTime.Now;
            trade_price = price;

        }

    }

}




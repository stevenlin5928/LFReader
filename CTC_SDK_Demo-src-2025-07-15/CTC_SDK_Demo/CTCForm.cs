#define USE_IR_CAN_SDK
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.IO;
using System.Collections;
using System.Threading.Tasks;
using System.Reflection;
using QT_CanUHF;
using Sunny.UI;
using System.Runtime.Serialization.Formatters.Binary;
using CTCDemo;
using FindEthernetCan;
using static QT_CanUHF.CanReader;
using System.Runtime.InteropServices;
namespace CommandDemo
{
    public partial class CTCForm : UIForm
    {
        // 导入Windows API
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 常量定义
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;

        // 修饰键标志
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;


        private IClient client = null;//发送客户端
        private delegate void crossThreadUpdateUI();
        const int calMax = 4;
        ArrayList caldatalist = new ArrayList();

        public CanReader irdevice = null;
        //send cmd
        bool bRepeatSendcmd = false; //true:启动定时发送指令； false:结束
        bool bRunningSendcmdThread = false; //true:发送指令线程正在运行； 结束运行
        Thread RepeatSendcmd_thread;

        //LF test
        bool bLF_TestRunning = false; //true: LF Test task正在运行; false: 已经结束运行
        
        private ManualResetEvent LF_RW_Ret_Done = new ManualResetEvent(false);
        DataTable tagdataTable = new DataTable();
        bool bConnecteStatus = false; //是否已经连线
        object lf_locker = new object(); //lf operation locker
        

        /**IR icon status
         *   -1：未知状态
         *   0：关闭
         *   1：打开
         */
        Dictionary<int, int> DicDevStatus = new Dictionary<int, int>();

        Queue<Byte[]> canDataQueque = new Queue<byte[]>();
        bool bIrDataProcess = false;
        //Queue<IRTrigger> IRTriggerQueque = new Queue<IRTrigger>();
        object ir_trigger_lock = new object(); //locker for IRTrigger 

        private Queue<TagData> tagDataQ = new Queue<TagData>();
        Thread tagQThread;
        bool btagQThreadExit = false;
        UInt32 getTagsCount = 0; //接收到标签次数
        bool bEPC_ASCII = false; //EPC是否已ascii字符显示，默认以HEX显示

        string userConfigFilePath = "userConfig.data"; //保存应用配置数据文件
        string userConfigOrg = ""; //初始值
        string userConfigLast = "";//最新值，与初始值比较是否需要保存

        /// <summary>
        /// 打印日志
        /// </summary>
        private void printLog(String logInfo)
        {
            string stamp = DateTime.Now.ToString("MM-dd HH:mm:ss ");
            crossThreadUpdateUI updateUI = delegate()
            {
                if (uicommandListBox.Items.Count > 1000)
                {
                    uicommandListBox.Items.RemoveAt(0);
                }
                uicommandListBox.Items.Add(stamp+logInfo);
                //if (commandListBox.SelectedIndex > -1)
                {
                    uicommandListBox.SelectedIndex = uicommandListBox.Items.Count - 1;
                }
            };
            try
            {
                this.connectBtn.Invoke(updateUI);
            }
            catch
            { }
        }

        private void IRDataReceive(byte[] packetData)
        {
            canDataQueque.Enqueue(packetData);
        }

        //防止DataGridView出现滚动条后 UI hang
        private delegate void UpdateDataGridView(DataRow dataRow);
        
        public void TagReport(RFID_EVENT rfidevent, TagData tag, int extra)
        {
            string stamp = DateTime.Now.ToUniversalTime().ToString();
            switch (rfidevent)
            {
                case RFID_EVENT.RFID_EVENT_INVENTORY_TAG:
                    if (!string.IsNullOrEmpty(tag.Rssi))//have rssi
                    {
                        printLog(String.Format("#{0}-{1} SN:{2} full EPC:{3} with rssi:{4}", tag.CanAddr.ToString("x"), stamp, tag.Tag_sn, tag.Epc, tag.Rssi));
                    }
                    else
                    {
                        //printLog(String.Format("#{0}-{1} SN:{2} full EPC:{3} no rssi", tag.CanAddr.ToString("x"), stamp, tag.Tag_sn, tag.Epc));
                    }
                    //
                    int ant = tag.Ant;
                    string ircan_uhf_addr = ((tag.CanAddr & 0x7f) + 1) + "_" + ant;

                    break;
                case RFID_EVENT.RFID_EVENT_INVENTORY_OVER:
                    {
                        printLog(String.Format("#{0} inventory over-total Tags:{1}", tag.CanAddr.ToString("x"), extra));
                    }
                    break;
                case RFID_EVENT.RFID_EVENT_ERR_INVALID_EPC_LENGTH:
                    {
                        printLog(String.Format("#{0} inventory EPC length :{1}", tag.CanAddr.ToString("x"), rfidevent));
                    }
                    break;
                case RFID_EVENT.RFID_EVENT_INVENTORY_TAG_CANREADER:
                    {
                        lock (tagDataQ) //把nfc card信息放到队列里 
                        {
                            tagDataQ.Enqueue(tag);
                        }
                        //printLog(String.Format("#{0} Tag epc:{1},tag_sn:{2}", tag.CanAddr.ToString("x"), tag.Epc,tag.Tag_sn));
                    }
                    break;
                default:
                    {
                        printLog(String.Format("#{0} unknow event :{1}", tag.CanAddr.ToString("x"), rfidevent));
                    }
                    break;
            }
        }

        public CTCForm()
        {
            InitializeComponent();


           
            //register event to receive scanned device
            FindEthernetCan.FindEthernetCan.mDevicesDiscoverHandler += OnDiscoverReceive;
            FindEthernetCan.FindEthernetCan.mDevicesDiscoverHandler_new += FindEthernetCan_mDevicesDiscoverHandler_new;
        }

        private void FindEthernetCan_mDevicesDiscoverHandler_new(FindEthernetCan.NetInfo netinfo)
        {
            //throw new NotImplementedException();
            this.Invoke(new Action(() =>
            {
                printLog($"Find device:{netinfo.Ip},{netinfo.Modelname}");
            }));
            return;
        }

        public void OnDiscoverReceive(string ip)
        {
            this.Invoke(new Action(() =>
            {
                comboBox_ip.Items.Add(ip);
                if (comboBox_ip.SelectedIndex == -1)
                    comboBox_ip.SelectedIndex = 0;
            }));
            return;
        }
        private void CommandForm_Load(object sender, EventArgs e)
        {
            //this.Text = $"{this.Text} Ver{Assembly.GetExecutingAssembly().GetName().Version}  SDK Ver{Get_SDK_Version()}"; //主窗口标题
            this.serialCb.DataSource = System.IO.Ports.SerialPort.GetPortNames();
            this.FormBorderStyle = FormBorderStyle.Sizable;
            //comboBox_IRtriggerStatus.SelectedIndex = 0; //启用
            this.comboBox_UART_Baudrate.SelectedIndex = 6; //115200
            
            serialCb.Enabled = true;
            comboBox_UART_Baudrate.Enabled = serialRb.Checked;
            comboBox_ip.Enabled = !serialRb.Checked;
            button_scan.Enabled = !serialRb.Checked;
            uiButton_register.Visible = false;

            //kafka info reload
            if (File.Exists(Application.StartupPath + "\\" + userConfigFilePath))
            {
                FileStream fs = new FileStream(Application.StartupPath + "\\" + userConfigFilePath, FileMode.Open, FileAccess.Read);
                BinaryFormatter bf = new BinaryFormatter();

                UserConfig user = (UserConfig)bf.Deserialize(fs);  //调用反序列化方法，从文件中读取对象信息
                
                fs.Close();   //关闭文件流
            }


            tagdataTable.Columns.Add("Line No.", typeof(int));   
            tagdataTable.Columns.Add("Addr", typeof(string));
            tagdataTable.Columns.Add("EPC", typeof(string));
            tagdataTable.Columns.Add("Count", typeof(int));
            tagdataTable.Columns.Add("TAG_SN", typeof(int));         
            tagdataTable.Columns.Add("UpdateTime", typeof(string));
            tagdataTable.Columns.Add("IP", typeof(string));


            /************双缓冲设置，防止UI闪烁*************/
            this.DoubleBuffered = true;//设置本窗体
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            this.UpdateStyles();

            //DataGridView 双缓冲设置，防止闪烁
            Setlanguage("Zh-CN");


            this.Text = $"LFR1M SDK Ver{Get_SDK_Version()}- APP Version 20251229.00";
            


            uiComboBox_barcode_mode.SelectedIndex = 1;
            uiComboBox_lf_page_start.SelectedIndex = 0;

        }
        private const String sdkPath = "C2CSDK.dll";
        public  String Get_SDK_Version()
        {
            try
            {
                System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(Application.StartupPath + "\\" + sdkPath);
                return info.ProductMajorPart.ToString() + '.' + info.ProductMinorPart + '.' + info.ProductBuildPart + '.' + info.ProductPrivatePart;
            }
            catch
            {
                printLog("get SDK version Fail!");
                return "?";
            }
        }


        private void connectBtn_Click(object sender, EventArgs e)
        {
#if USE_IR_CAN_SDK
            //if (irdevice != null)
            //{
            //    if (irdevice.IsDeviceConnected())
            //    {
            //        irdevice.CloseDevice();
            //    }
            //}
            irdevice = new CanReader();
            Result_t result = Result_t.OK;
            if (serialRb.Checked)
            {
                result = irdevice.OpenSerialDevice(this.serialCb.Text, int.Parse(comboBox_UART_Baudrate.Text));
            }
            else
            {
                result = irdevice.OpenDevice(this.comboBox_ip.Text);
            }

            if (result == Result_t.OK)
            {
                irdevice.irprocessfuc += IRDataReceive;
                irdevice.tagreport += TagReport;
                //irdevice.lfTagReport += Irdevice_lfTagReport;
                this.connectBtn.Enabled = false;
                this.disconnectBtn.Enabled = true;
                this.mainPanel.Enabled = true;
               
                bConnecteStatus = true;
                //groupBox_devicelist.Visible = true;
                Application.DoEvents();
                //irdevice.CAN_UHF_search();

                uiIntegerUpDown_lf_count.Value = 10;
                uiComboBox_lf_select.Items.Clear();
                uiComboBox_lf_select.Items.Add("ANT#1");
                uiComboBox_lf_select.Items.Add("ANT#2");
                uiComboBox_lf_select.Items.Add("ANT#3");
                uiComboBox_lf_select.Items.Add("ANT#4");
                uiComboBox_lf_select.SelectedIndex = 0;
            }
            else
            {
                MessageBox.Show("Unable to connect reader!");
            }
            //this.connectBtn.Enabled = false;
            //this.disconnectBtn.Enabled = true;
            //this.mainPanel.Enabled = true;
#else
            if (serialRb.Checked)
            {
                client = new SerialClient(this.serialCb.Text);
            }
            else
            {
                client = new TCPClient(this.comboBox_ip.Text,20001);
            }

            bool connect = client.Connect();
            if (connect)
            {
                Thread receiveThread = new Thread(ReceivePacket);
                receiveThread.Start();


                this.connectBtn.Enabled = false;
                this.disconnectBtn.Enabled = true;
                this.mainPanel.Enabled = true;
                groupBox_devicelist.Visible = true;

            }
            else
            {
                MessageBox.Show("无法连接读写器");
            }
#endif
            //FindEthernetCan.FindEthernetCan.StopDiscovery();
        }


        private void disconnectBtn_Click(object sender, EventArgs e)
        {

            //最后关闭连接
            if (irdevice != null)
            {
                irdevice.tagreport -= TagReport;
                //irdevice.lfTagReport -= Irdevice_lfTagReport;
                irdevice.CloseDevice();
            }

            this.connectBtn.Enabled = true;
            this.disconnectBtn.Enabled = false;
            this.mainPanel.Enabled = false;
            bConnecteStatus = false;

        }

        /// <summary>
        /// 清空日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void clearLogBtn_Click(object sender, EventArgs e)
        {
            this.uicommandListBox.Items.Clear();
        }

        /// <summary>
        /// 发送自定义命令
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void sendBtn_Click(object sender, EventArgs e)
        {
            if (String.IsNullOrEmpty(commandTb.Text))
            {
                MessageBox.Show("CMD is empty!");
                return;
            }
            String msg = commandTb.Text.Replace(" ", "");
            if (msg.Length % 2 == 1)
            {
                MessageBox.Show("The input data is incorrect!");
                return;
            }

            if (Util.IsIllegalHexadecimal(msg))
            {
                MessageBox.Show("Not a hexadecimal string!");
                return;
            }
           
            UInt16 addr = 0;
            irdevice.send_Customer_message( msg);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 注册热键 Alt+Ctrl+Shift+K
            bool success = RegisterHotKey(
                this.Handle,
                HOTKEY_ID,
                MOD_ALT | MOD_CONTROL | MOD_SHIFT,
                (int)Keys.K
            );

            if (!success)
            {
                MessageBox.Show("热键注册失败，可能已被其他程序占用。");
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_HOTKEY)
            {
                if (m.WParam.ToInt32() == HOTKEY_ID)
                {
                    // 热键触发后的操作
                    //MessageBox.Show("组合键 Alt+Ctrl+Shift+K 被按下！");
                    uiButton_register.Visible = !uiButton_register.Visible;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // 卸载热键
            UnregisterHotKey(this.Handle, HOTKEY_ID);
        }

        private void CommandForm_FormClosing(object sender, FormClosingEventArgs e)
        {
#if false
            if (client != null && client.IsAlive())
            {

                Thread.Sleep(200);
                //最后关闭连接
                client.DisConnect();
            }
#endif
            //bRunningHBThread = false;
            bRunningSendcmdThread = false;
            if (irdevice != null)
            {
                irdevice.CloseDevice();
            }
            bIrDataProcess = false;
            btagQThreadExit = true;
            FindEthernetCan.FindEthernetCan.StopDiscovery();

            if (bLF_TestRunning)  //LF test task exit
                bLF_TestRunning = false;

            //EthCANDemo.Properties.Settings.Default.Save();

        }

        private void button_getCANIRDevice_Click(object sender, EventArgs e)
        {
#if false
            comboBox_device.Items.Clear();
            for (int i=0;i< label_control_array.Length;i++)
            {
                Label obj = (Label)label_control_array[i];
                Image img = Image.FromFile(@"status-gray.png");
                obj.Image = img.Clone() as Image;
                obj.Size = img.Size;
                img.Dispose();
            }
            Application.DoEvents();
            byte[] data = new byte[5] {0xA5,0x5A,0xFF,0xFF,0x88 };
            SendCommand(0x0789,data);
#else
            //comboBox_device.Items.Clear();
            //irDevTable.Rows.Clear(); 
            //for (int i = 0; i < label_control_array.Length; i++)
            //{
            //    Label obj = (Label)label_control_array[i];
            //    Image img = Image.FromFile(@"status-gray.png");
            //    obj.Image = img.Clone() as Image;
            //    obj.Size = img.Size;
            //    img.Dispose();
            //}
            Application.DoEvents();
            //irdevice.get_IR_AllDevices();
            irdevice.CAN_UHF_search();
#endif
        }

        private void btn_reboot_Click(object sender, EventArgs e)
        {
            Int32 status =-1;
            Result_t ret = irdevice.CTC_reboot(out status); ;
            if (ret == Result_t.ERR_TIMEOUT)
            {
                printLog("Reboot timeout!");
            }
            else
            {
                printLog(String.Format("Reboot OK! {0}", status));

            }
        }

        private void button_fwVersion_Click(object sender, EventArgs e)
        {
            UInt16 addr = 0;
            button_fwVersion.Enabled = false;

            Task t = new Task(() =>
            {
                String version = "";
                Result_t ret = irdevice.CTC_FW_Version(out version);
                if (ret == Result_t.OK)
                {
                    printLog(String.Format("#{0},CTC Firmware version:{1}", addr.ToString("X3"), version));
                }
                else
                {
                    printLog(String.Format("#{0},获取CTC Firmware，错误码:{0}", addr.ToString("X3"), ret));
                }
            });
            t.Start();

            button_fwVersion.Enabled = true;
        }

        private void button_watchdog_Click(object sender, EventArgs e)
        {
            int watchdogstatus = 0;

            Result_t result = irdevice.CTC_Watchdog_Status(out watchdogstatus);
            //int device_code = (addr & 0x7f) + 1;
            if (result == Result_t.OK)
            {
                printLog(String.Format("watchdog status:{0}", watchdogstatus));
            }
            else
            {
                printLog(String.Format("获取watchdog错误，错误码:{0}", result));
            }
            button_watchdog.Enabled = true;
        }

        private void button_inv_Click(object sender, EventArgs e)
        {
#if false
            byte[] data = new byte[7] { 0xA5, 0x5A, 0xFF, 0xFF, 0xA0,0x02,0x0A };
            String selDev = "";
            Int16 addr = 0;
            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("请选择设备!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];


            DictTagEPCsegments.Clear(); //开始新的盘点前，先清除DictTagEPCsegments中的数据
            byte ant =Convert.ToByte( textBox_ant.Text);
            byte time = byte.Parse(textBox_invtine.Text);

            Pr9xAntenna pr9x_ant = UserAnt2Pr9xAntenna(ant); //转化成pr92固件识别的天线
            addr = Convert.ToInt16(selDev, 16);
            data[2] = (byte)((addr >> 8) & 0xff);
            data[3] = (byte)((addr >> 0) & 0xff);
            data[5] = (byte)pr9x_ant;
            data[6] = time;
            SendCommand(0x0789, data);
#else
            
#endif
        }

        private void label_device1_MouseDown(object sender, MouseEventArgs e)
        {
            //Label label = (Label)sender;

            //MessageBox.Show(label.Text);
        }

        private void button_scan_Click(object sender, EventArgs e)
        {
            comboBox_ip.Items.Clear();
            FindEthernetCan.FindEthernetCan.StartDiscovery();
        }

        private void checkBox_repeatSend_CheckedChanged(object sender, EventArgs e)
        {
            bRepeatSendcmd = checkBox_repeatSend.Checked;
            if (bRepeatSendcmd == false)
            {
                if (bRunningSendcmdThread) //线程正在运行
                {
                    RepeatSendcmd_thread.Abort();//线程中止运行
                }
                bRunningSendcmdThread = false;
            }
            else
            {
                if (bRunningSendcmdThread == false)
                {
                    RepeatSendcmd_thread = new Thread(RepeatSendcmd_thrMethod);
                    RepeatSendcmd_thread.IsBackground = true;
                    RepeatSendcmd_thread.Start();
                }
            }
        }

        private void RepeatSendcmd_thrMethod()
        {
            bRunningSendcmdThread = true;//线程开始运行

            try
            {
                int looptime = Convert.ToInt32(textBox_looptime.Text.Trim());
                while (bRepeatSendcmd)
                {
                    this.Invoke(new Action(() =>
                    {
                        if (client.IsAlive() == true) //如果断线就不用发送了
                        {
                            sendBtn_Click(null, null);
                        }
                    }));
                    Thread.Sleep(looptime * 1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            bRunningSendcmdThread = false; //线程结束
        }

        private void setAntPowerBtn_Click(object sender, EventArgs e)
        {
#if false
            int antPower = (int)ant1PowerNb.Value;

            byte[] data = new byte[6] { 0xA5, 0x5A, 0xFF, 0xFF, 0xA2, 0x22};
            String selDev = "";
            Int16 addr = 0;
            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("请选择设备!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            addr = Convert.ToInt16(selDev, 16);
            data[2] = (byte)((addr >> 8) & 0xff);
            data[3] = (byte)((addr >> 0) & 0xff);
            data[5] = (byte)antPower;
            SendCommand(0x0789, data);
#else
            
#endif
        }

        private void getAntPowerBtn_Click(object sender, EventArgs e)
        {
#if false
            byte[] data = new byte[5] { 0xA5, 0x5A, 0xFF, 0xFF, 0xA3};
            String selDev = "";
            Int16 addr = 0;
            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("请选择设备!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            addr = Convert.ToInt16(selDev, 16);
            data[2] = (byte)((addr >> 8) & 0xff);
            data[3] = (byte)((addr >> 0) & 0xff);
            SendCommand(0x0789, data);
#else
            
#endif
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Data">4byte array:rssi_i,rssi_q,gain_i,gain_q</param>
        /// <returns></returns>
        private string CalcTagRssi(byte[] rssiData)
        {
            double tag_rssi;
            int rssi_i;
            int rssi_q;
            int gain_i;
            int gain_q;
            double rfin_i;
            double rfin_q;

            if (rssiData.Length != 4)
            {
                throw new Exception("RSSIData Length not equal to 4!");
            }

            rssi_i = rssiData[0];
            rssi_q = rssiData[1];
            gain_i = rssiData[2];
            gain_q = rssiData[3];

            rfin_i = (20 * Math.Log10(rssi_i) - gain_i - 33 - 30);
            rfin_q = (20 * Math.Log10(rssi_q) - gain_q - 33 - 30);

            rfin_i = Math.Pow(10, (rfin_i / 20));
            rfin_q = Math.Pow(10, (rfin_q / 20));

            tag_rssi = Math.Sqrt(Math.Pow(rfin_i, 2) + Math.Pow(rfin_q, 2));

            return String.Format("{0:0.0}", 20 * Math.Log10(tag_rssi));
        }

        private void button_uhfCheck_Click(object sender, EventArgs e)
        {
#if false
            byte[] data = new byte[5] { 0xA5, 0x5A, 0xFF, 0xFF, 0xA4 };
            String selDev = "";
            Int16 addr = 0;

            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("请选择设备!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];

            addr = Convert.ToInt16(selDev, 16);
            data[2] = (byte)((addr >> 8) & 0xff);
            data[3] = (byte)((addr >> 0) & 0xff);
            SendCommand(0x0789, data); //UHF 上电
            Thread.Sleep(1000);     //等待1s
            data[4] = 0xA6; // 查询UHF 是否存在
            SendCommand(0x0789, data);
#else
            
#endif
        }

        private void commandListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
#if false
            if (e.Index >= 0)
            {
                e.DrawBackground();
                Brush mybsh = Brushes.Black;
                // 判断是什么类型的item
                if (uicommandListBox.Items[e.Index].ToString().IndexOf("page") != -1)
                {
                    //mybsh = Brushes.BlueViolet;
                    mybsh = Brushes.Red;
                }
                else if (uicommandListBox.Items[e.Index].ToString().IndexOf("err") != -1)
                {
                    mybsh = Brushes.Red;
                }
                // 焦点框
                e.DrawFocusRectangle();
                //文本 
                e.Graphics.DrawString(uicommandListBox.Items[e.Index].ToString(), e.Font, mybsh, e.Bounds, StringFormat.GenericDefault);
            }  
#endif
        }
      
        private void serialRb_CheckedChanged(object sender, EventArgs e)
        {
            serialCb.Enabled = serialRb.Checked;
            comboBox_UART_Baudrate.Enabled = serialRb.Checked;
            if (serialRb.Checked)
            {
                this.serialCb.DataSource = System.IO.Ports.SerialPort.GetPortNames(); //刷新串口item
            }
            comboBox_ip.Enabled = !serialRb.Checked;
            button_scan.Enabled = !serialRb.Checked;
        }

        private void uiRadioButton_RedLED_Click(object sender, EventArgs e)
        {
            LED_STATE cmdcode = LED_STATE.LED_OFF;
            if (uiRadioButton_RedLEDOff.Checked)
                cmdcode = LED_STATE.LED_OFF;

            if (uiRadioButton_RedLEDOn.Checked)
                cmdcode = LED_STATE.LED_ON;

            if (uiRadioButton_RedLEDFlash.Checked)
                cmdcode = LED_STATE.LED_FLASH;

            irdevice.CTC_SetLed(LED_TYPE.LED_RED ,cmdcode);
        }

        private void uiRadioButton_GreenLED_Click(object sender, EventArgs e)
        {
            LED_STATE cmdcode = LED_STATE.LED_OFF;
            if (uiRadioButton_GreenLEDOff.Checked)
                cmdcode = LED_STATE.LED_OFF;

            if (uiRadioButton_GreenLEDOn.Checked)
                cmdcode = LED_STATE.LED_ON;

            if (uiRadioButton_GreenLEDFlash.Checked)
                cmdcode = LED_STATE.LED_FLASH;

            irdevice.CTC_SetLed(LED_TYPE.LED_GREEN, cmdcode);
        }

        private void uiRadioButton_BlueLED_Click(object sender, EventArgs e)
        {
            LED_STATE cmdcode = LED_STATE.LED_OFF;
            if (uiRadioButton_BlueLEDOff.Checked)
                cmdcode = LED_STATE.LED_OFF;

            if (uiRadioButton_BlueLEDOn.Checked)
                cmdcode = LED_STATE.LED_ON;

            if (uiRadioButton_BlueLEDFlash.Checked)
                cmdcode = LED_STATE.LED_FLASH;

            irdevice.CTC_SetLed(LED_TYPE.LED_BLUE, cmdcode);
        }

        private void EthCANForm_KeyDown(object sender, KeyEventArgs e)
        {
            //Console.WriteLine($"keydown:{e.Control},{e.Control},{e.KeyCode}");
            if (e.Control && e.Alt && e.KeyCode == Keys.D8)
            {
                //groupBox_testLF.Visible = !groupBox_testLF.Visible;
                //Console.WriteLine("switch testLF visible");
            }
            else if (e.Control && e.Alt && e.KeyCode == Keys.D9)
            {
                //button_writeLF.Visible = !button_writeLF.Visible;
            }
        }


        /// <summary>
        /// 设定APP 语言
        /// </summary>
        /// <param name="locale">"Zh-TW","zh-CN,"en-US""</param>
        private void Setlanguage(String locale)
        {
            //CHS_ToolStripMenuItem.Checked = String.Equals(locale, "zh-CN")? true : false;
            //CHT_ToolStripMenuItem.Checked = String.Equals(locale, "Zh-TW") ? true : false;;
            //SaveLanguage();
            Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(locale);
            LanguageHelper.SetLang(locale, this, this.GetType());
        }


        private void uiButton_lf_stop_Click(object sender, EventArgs e)
        {
            bLF_TestRunning=false;
            uiButton_lf_read.Enabled = true;
        }
        private void uiButton_lf_read_Click(object sender, EventArgs e)
        {
            int index = uiComboBox_lf_select.SelectedIndex;
            int pageNum = uiComboBox_lf_page_start.SelectedIndex+1;
            Result_t ret = Result_t.ERR_FAILED;

            var max_count = uiIntegerUpDown_lf_count.Value;
            if(bLF_TestRunning)
            {
                MessageBox.Show("on test....");
                return;
            }
            uiButton_lf_read.Enabled = false;
            bLF_TestRunning=true;
            uiTextBox_lf_data.Text = "";
            int _read_loop_count = 0;

            Application.DoEvents();
            Task t = new Task(() =>
            {
                String lfdada = "";
                UInt32 succ_count = 0;
                UInt32 failed_count = 0;
                for(int i=0;i<max_count;i++)
                {
                    if(!bLF_TestRunning)
                    {
                        printLog("Stop Read!");
                        break;
                    }

                    for(int p = 0; p < 6; p++)
                    {
                        _read_loop_count = p + 1;
                        ret = irdevice.CTC_Read_LF(index, pageNum, 1, out lfdada);
                        if (ret == Result_t.OK)
                        {
                            break;
                        }
                            

                    }

                    if (ret == Result_t.OK)
                    {
                        succ_count++;
                        this.Invoke(new Action(() =>
                        {
                            if(uiCheckBox_lf_hex.Checked==true)
                            {
                                uiTextBox_lf_data.Text= lfdada;
                            }
                            else
                            {
                                uiTextBox_lf_data.Text = System.Text.Encoding.Default.GetString( Util.ToHexByte(lfdada));
                            }
                                            
                        }));

                        printLog($"{lfdada}--{_read_loop_count}");
                        Console.Beep();
                    }
                    else
                    {
                        printLog(String.Format("read LF failed，ERROR,{0}", ret));
                        failed_count++;
                    }
                    Thread.Sleep(5);
                    this.Invoke(new Action(() =>
                    {
                        uiLabel_succ_count.Text = $"{succ_count}/{i+1}" ;
                    }));
                    Application.DoEvents();
                }
                bLF_TestRunning = false;
            });
            t.Start();

            uiButton_lf_read.Enabled = true;
            return;
        }

        private void uiButton_barcode_read_Click(object sender, EventArgs e)
        {
            int trigger = uiComboBox_barcode_mode.SelectedIndex;
            uiButton_barcode_read.Enabled = false;
            uiTextBox_barcode.Text = "";
            Application.DoEvents();

            Task t = new Task(() =>
            {
                Result_t ret = irdevice.CTC_Read_Barcode(trigger);
                if (ret == Result_t.OK)
                {
                    printLog(String.Format("read Barcode OK:{0}",ret));
                }
                else
                {
                    printLog(String.Format("read Barcode failed，ERROR,{0}", ret));
                }
            });
            t.Start();
            uiButton_barcode_read.Enabled = true;
            return;
        }


        private void uiCheckBox_lf_hex_CheckedChanged(object sender, EventArgs e)
        {
            bool bhex = uiCheckBox_lf_hex.Checked;
            string msg = uiTextBox_lf_data.Text;
            if (!String.IsNullOrEmpty(msg))
                uiTextBox_lf_data.Text=(bhex == false) ? Encoding.ASCII.GetString(Util.ToHexByte(msg)) : Util.ToHexString(Encoding.ASCII.GetBytes(msg));
        }

        private void uiButton_oled_Click(object sender, EventArgs e)
        {
            try
            {
                bool bmode = uiRadioButton_mode.Checked;
                int align = 0;
                if (uiRadioButton_align_left.Checked)
                    align = 0;
                else if (uiRadioButton_align_middle.Checked)
                    align = 1;
                else if (uiRadioButton_align_right.Checked)
                    align = 2;
                int fontsize = 0;
                if (uiRadioButton_fontsize_8x16.Checked)
                    fontsize = 1;
                else if(uiRadioButton_fontsize_6x8.Checked)
                    fontsize = 0;
                int x = int.Parse(uiTextBox_x_axis.Text.ToString());
                int y = int.Parse(uiTextBox_y_axis.Text.ToString());
                string msg = uiTextBox_oled_msg.Text.ToString();
                Result_t result =irdevice.CTC_Show_Oled_text(bmode, align, fontsize,x,y,msg);
                if(result==Result_t.OK)
                {
                    printLog(String.Format("Show OLED {0} OK", msg));
                }
                else
                    printLog(String.Format("Show OLED {0} Failed", msg));

            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);

            }
            
        }

        private void uiButton_gpi_get_Click(object sender, EventArgs e)
        {
            bool checked_1 = uiCheckBox_gpi_in_1.Checked;
            bool checked_2 = uiCheckBox_gpi_in_2.Checked;
            bool checked_3 = uiCheckBox_gpi_in_3.Checked;
            bool checked_4 = uiCheckBox_gpi_in_4.Checked;
            if(!checked_1&& !checked_2 && !checked_3 && !checked_4 )
            {
                MessageBox.Show("Please select input checkbox!");
                return;
            }
            if(checked_1)
            {
                int value = 0;
                uiLabel_gpi_in_1.Text = "-----";
                if (irdevice.gpio_get_gpi(0, out value)==Result_t.OK)
                {
                    uiLabel_gpi_in_1.Text = (value==1)?"High":"Low";
                }
                else
                {
                    MessageBox.Show("Failed to get input 1 !");
                }
            }
            if (checked_2)
            {
                int value = 0;
                uiLabel_gpi_in_2.Text = "-----";
                if (irdevice.gpio_get_gpi(1, out value) == Result_t.OK)
                {
                    uiLabel_gpi_in_2.Text = (value == 1) ? "High" : "Low"; ;
                }
                else
                {
                    MessageBox.Show("Failed to get input 2 !");
                }
            }
            if (checked_3)
            {
                int value = 0;
                uiLabel_gpi_in_3.Text = "-----";
                if (irdevice.gpio_get_gpi(2, out value) == Result_t.OK)
                {
                    uiLabel_gpi_in_3.Text = (value == 1) ? "High" : "Low"; ;
                }
                else
                {
                    MessageBox.Show("Failed to get input 3 !");
                }
            }
            if (checked_4)
            {
                int value = 0;
                uiLabel_gpi_in_4.Text = "-----";
                if (irdevice.gpio_get_gpi(3, out value) == Result_t.OK)
                {
                    uiLabel_gpi_in_4.Text = (value == 1) ? "High" : "Low"; ;
                }
                else
                {
                    MessageBox.Show("Failed to get input 4 !");
                }


            }
        }

        private void uiButton_gpo_set_Click(object sender, EventArgs e)
        {
            bool checked_1 = uiCheckBox_gpo_1.Checked;
            bool checked_2 = uiCheckBox_gpo_2.Checked;
            bool checked_3 = uiCheckBox_gpo_3.Checked;
            bool checked_4 = uiCheckBox_gpo_4.Checked;
            if (!checked_1 && !checked_2 && !checked_3 && !checked_4)
            {
                MessageBox.Show("Please select input checkbox!");
                return;
            }
            if (checked_1)
            {
                int value = 0;
                if(uiComboBox_gpo_1.SelectedIndex == 0)
                    value = 0;
                else if (uiComboBox_gpo_1.SelectedIndex == 1)
                    value = 1;
                if (irdevice.gpio_set_gpo(1, value) == Result_t.OK)
                {
                    
                }
                else
                {
                    MessageBox.Show("Failed to set output 1 !");
                }
            }
            if (checked_2)
            {
                int value = 0;
                if (uiComboBox_gpo_2.SelectedIndex == 0)
                    value = 0;
                else if (uiComboBox_gpo_2.SelectedIndex == 1)
                    value = 1;
                if (irdevice.gpio_set_gpo(2, value) == Result_t.OK)
                {
                    
                }
                else
                {
                    MessageBox.Show("Failed to set output 2 !");
                }
            }
            if (checked_3)
            {
                int value = 0;
                if (uiComboBox_gpo_3.SelectedIndex == 0)
                    value = 0;
                else if (uiComboBox_gpo_3.SelectedIndex == 1)
                    value = 1;
                if (irdevice.gpio_set_gpo(3,  value) == Result_t.OK)
                {

                }
                else
                {
                    MessageBox.Show("Failed to set output 3 !");
                }
            }
            if (checked_4)
            {
                int value = 0;
                if (uiComboBox_gpo_4.SelectedIndex == 0)
                    value = 0;
                else if (uiComboBox_gpo_4.SelectedIndex == 1)
                    value = 1;
                if (irdevice.gpio_set_gpo(4, value) == Result_t.OK)
                {
                   
                }
                else
                {
                    MessageBox.Show("Failed to set output 4 !");
                }


            }
        }

        private void uiButton_COM_TX_Click(object sender, EventArgs e)
        {
            int port = 0;
            string msg = uiTextBox_data_tx_msg.Text;
            if (String.IsNullOrEmpty(msg))
            {
                MessageBox.Show("TX data is empty");
                return;
            }
            if (uiComboBox_com_text_port.SelectedIndex>-1)
            {
                if (uiComboBox_com_text_port.SelectedIndex == 0)
                    port = 1;
                else if (uiComboBox_com_text_port.SelectedIndex == 1)
                    port = 2;
                if (irdevice.uart_tx_test(port,msg) == Result_t.OK)
                {

                }
                else
                {
                    MessageBox.Show("Failed to set output 4 !");
                }

            }
            else
            {

                MessageBox.Show("Select COM port");
            }
        }

        private void uiButton_COM_RX_TEST_Click(object sender, EventArgs e)
        {
            int port = 0;

            if (uiComboBox_com_text_port.SelectedIndex > -1)
            {
                if (uiComboBox_com_text_port.SelectedIndex == 0)
                    port = 1;
                else if (uiComboBox_com_text_port.SelectedIndex == 1)
                    port = 2;
                if (irdevice.uart_rx_test(port,true) == Result_t.OK)
                {

                }
                else
                {
                    MessageBox.Show("Failed to set output 4 !");
                }

            }
            else
            {

                MessageBox.Show("Select COM port");
            }
        }

        private void uiButton_COM_RX_TEST_stop_Click(object sender, EventArgs e)
        {
            int port = 0;

            if (uiComboBox_com_text_port.SelectedIndex > -1)
            {
                if (uiComboBox_com_text_port.SelectedIndex == 0)
                    port = 1;
                else if (uiComboBox_com_text_port.SelectedIndex == 1)
                    port = 2;
                if (irdevice.uart_rx_test(port, false) == Result_t.OK)
                {

                }
                else
                {
                    MessageBox.Show("Failed to set output 4 !");
                }

            }
            else
            {

                MessageBox.Show("Select COM port");
            }
        }

        private void uiCheckBox_gpi_all_CheckedChanged(object sender, EventArgs e)
        {
            if (uiCheckBox_gpi_all.Checked == true)
            {
                uiCheckBox_gpi_in_1.Checked = true;
                uiCheckBox_gpi_in_2.Checked = true;
                uiCheckBox_gpi_in_3.Checked = true;
                uiCheckBox_gpi_in_4.Checked = true;

            }
            else
            {
                uiCheckBox_gpi_in_1.Checked = false;
                uiCheckBox_gpi_in_2.Checked = false;
                uiCheckBox_gpi_in_3.Checked = false;
                uiCheckBox_gpi_in_4.Checked = false;
            }
        }

        private void uiCheckBox_gpo_all_CheckedChanged(object sender, EventArgs e)
        {
            if (uiCheckBox_gpo_all.Checked == true)
            {
                uiCheckBox_gpo_1.Checked = true;
                uiCheckBox_gpo_2.Checked = true;
                uiCheckBox_gpo_3.Checked = true;
                uiCheckBox_gpo_4.Checked = true;

            }
            else
            {
                uiCheckBox_gpo_1.Checked = false;
                uiCheckBox_gpo_2.Checked = false;
                uiCheckBox_gpo_3.Checked = false;
                uiCheckBox_gpo_4.Checked = false;
            }
        }

        private void uiButton_get_trigger_enable_Click(object sender, EventArgs e)
        {
            bool b_trigger_enable = false;
            byte lf_page_start = 1;
            byte lf_read_count = 14;
            if (irdevice.get_LF_trigger_enable(out b_trigger_enable,out lf_page_start,out lf_read_count) == Result_t.OK)
            {
                uiCheckBox_triigger_status.Checked = b_trigger_enable;
                uiComboBox_lf_page_start.SelectedIndex =(lf_page_start==0)?0:(lf_page_start-1);
                uiTextBox_lf_count.Text=lf_read_count.ToString();
            }
            else
            {
                MessageBox.Show("Failed to set output 4 !");
            }
        }
        private void uiButton_set_trigger_enable_Click(object sender, EventArgs e)
        {
            bool b_trigger_enable = false;
            byte lf_page_start = 1;
            byte lf_read_count = 14;
            if (uiCheckBox_triigger_status.Checked == true)
                b_trigger_enable = true;
            else
                b_trigger_enable = false;
            if(uiComboBox_lf_page_start.SelectedIndex==-1)
            {
                MessageBox.Show("Select page first!");
                return;
            }
            lf_page_start = (byte)(uiComboBox_lf_page_start.SelectedIndex + 1);
            string count_str = uiTextBox_lf_count.Text;
            if(string.IsNullOrEmpty(count_str))
            {
                MessageBox.Show("Input count first!");
                return;
            }
            try
            {
                lf_read_count=byte.Parse(count_str);
            }
            catch
            {
                MessageBox.Show("Invalid count!");
                return;
            }

            if (irdevice.set_LF_trigger_enable(b_trigger_enable, lf_page_start, lf_read_count) == Result_t.OK)
            {
                uiCheckBox_triigger_status.Checked = b_trigger_enable;
            }
            else
            {
                MessageBox.Show("Failed to set triger !");
            }
        }


        private void uiButton_register_Click(object sender, EventArgs e)
        {
            byte[] data = new byte[] { 0xFF,0x00,0x06,0xC2,0xE5,0x8A,0x05,0x12,0x3E };
            irdevice.send_Customer_message(data);
        }

        private void uiButton_get_secs_lf_ant_base_Click(object sender, EventArgs e)
        {
            byte lf_ant_base = 0;
            if(irdevice.get_LF_ANT_base(out lf_ant_base) == Result_t.OK)
            {
                uiComboBox_SECS_LF_ant.SelectedIndex = lf_ant_base;
            }
            else
            {
                MessageBox.Show("Failed to set secs ant base report !");
            }
        }

        private void uiButton_set_secs_lf_ant_base_Click(object sender, EventArgs e)
        {
            byte lf_ant_base = (byte)uiComboBox_SECS_LF_ant.SelectedIndex;
            if (irdevice.set_LF_ANT_base(lf_ant_base) == Result_t.OK)
            {
                uiComboBox_SECS_LF_ant.SelectedIndex = lf_ant_base;
            }
            else
            {
                MessageBox.Show("Failed to set secs ant base report !");
            }
        }

        private void uiButton_get_mac_Click(object sender, EventArgs e)
        {
            string macstr="";
            if (irdevice.get_MAC(out macstr) == Result_t.OK)
            {
                uiTextBox_device_mac.Text = macstr;
            }
            else
            {
                MessageBox.Show("Failed to set secs ant base report !");
            }
        }

        private void uiButton_set_mac_Click(object sender, EventArgs e)
        {
            string macstr = uiTextBox_device_mac.Text;
            if (irdevice.set_mac(macstr) == Result_t.OK)
            {
               
            }
            else
            {
                MessageBox.Show("Failed to set secs ant base report !");
            }
        }

        private void uiButton_Cls_Click(object sender, EventArgs e)
        {
            uicommandListBox.Items.Clear();
        }

        private void serialRb_CheckedChanged_1(object sender, EventArgs e)
        {

        }
    }

}

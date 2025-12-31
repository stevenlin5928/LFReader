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
        

        /// <summary>
        /// 解析封包
        /// </summary>
        /// <param name="packetData"></param>
        private void IRDataprocess()
        {
            while (bIrDataProcess)
            {
                if (canDataQueque.Count > 0)
                {
                    byte[] packetData = canDataQueque.Dequeue();
                    if (packetData == null)
                    {
                        Console.WriteLine("packetData==null");
                        continue;
                    }
                    int dataLength = ((packetData[1]<<8)+ packetData[2])+6;// 包长度
                    
                    int cmdcode = packetData[3];

                    switch (cmdcode)
                    {

                        case (int)0x01:
                            {
                                String hw_version = System.Text.Encoding.Default.GetString(packetData, 6, 4);
                                String fw_version = System.Text.Encoding.Default.GetString(packetData, 14, 4);
                                UInt16 status =(UInt16) ((packetData[4] << 8) + packetData[5]);
                                //printLog($"#{addr.ToString("X3")},CAN Reader Version:{version}");
                                printLog(String.Format("HW version:{0} FW version:{1},Status:0x{2:X}", hw_version, fw_version, status));
                            }
                            break;
                        case (Int32)CTC_CMD_RESP.CTC_CMD_READ_BARCODE:
                            {
                                UInt16 status = (UInt16)((packetData[4] << 8) + packetData[5]);
                                UInt16 barf_len = (UInt16)((packetData[1] << 8) + packetData[2]);
                                if (status == 0)
                                {
                                    //byte[] dst = new byte[barf_len - 2];
                                    //Array.Copy(packetData, 6, dst, 0, barf_len - 2);
                                    this.Invoke(new Action(() =>
                                    {
                                        uiTextBox_barcode.Text = System.Text.Encoding.Default.GetString(packetData, 6, barf_len - 2);
                                    }));
                                }

                            }
                            break;
                        case (Int32)CTC_CMD_RESP.CTC_CMD_SET_MOTOR_ACTION:
                        {
                                UInt16 status = (UInt16)((packetData[4] << 8) + packetData[5]);
                                UInt16 mode = (UInt16)(packetData[6]);
                                if (status == 0)
                                {
                                    //byte[] dst = new byte[barf_len - 2];
                                    //Array.Copy(packetData, 6, dst, 0, barf_len - 2);
                                    this.Invoke(new Action(() =>
                                    {
                                        if(mode==1||mode==2||mode==3)
                                        {
                                            printLog(String.Format("电机 {0} 成功", (mode == 1) ? "搬运" : (mode == 2) ? "返回原点" : "搬运->返回原点"));
                                        }
                                        else if(mode==4)
                                        {
                                            printLog(String.Format("电机停止运行"));
                                        }
                                    }));
                                }
                                else
                                {
                                    //byte[] dst = new byte[barf_len - 2];
                                    //Array.Copy(packetData, 6, dst, 0, barf_len - 2);
                                    this.Invoke(new Action(() =>
                                    {
                                        printLog(String.Format("电机运行失败，status:{0},mode:{1}",status, mode));
                                    }));
                                }


                            }
                        break;
                        case (Int32)CTC_CMD_RESP.GPIO_GPI_CMD:
                            {
                                UInt16 status = (UInt16)((packetData[4] << 8) + packetData[5]);
                                byte pin_num = packetData[6];
                                byte level = packetData[7];
                                byte retport_type = packetData[8];

                                /*0:Unsolicited 主动上报;
                                1:solicited 被动上报
                                */
                                if (retport_type == 1)
                                {

                                }
                                else
                                {
                                    this.Invoke(new Action(() =>
                                    {
                                        switch(pin_num)
                                        {
                                            case 1:
                                                {
                                                    uiLabel_gpi_in_1.Text = (level == 1) ? "High" : "Low";
                                                }
                                                break;
                                            case 2:
                                                {
                                                    uiLabel_gpi_in_2.Text = (level == 1) ? "High" : "Low";
                                                }
                                                break;
                                            case 3:
                                                {
                                                    uiLabel_gpi_in_3.Text = (level == 1) ? "High" : "Low";
                                                }
                                                break;
                                            case 4:
                                                {
                                                    uiLabel_gpi_in_4.Text = (level == 1) ? "High" : "Low";
                                                }
                                                break;
                                            default:
                                                {
                                                    printLog(String.Format("unknown GPI report:{0},PIN_{1},{2}", (retport_type == 0) ? "unsonicited" : "sonicited", pin_num, (level == 1) ? "High" : "Low"));
                                                }
                                                break;
                                        }
                                    }));
                                    printLog(String.Format("GPI report:{0},PIN_{1},{2}", (retport_type==0)?"unsonicited":"sonicited", pin_num, (level==1)?"High":"Low"));
                                }
                            }
                            break;
                        case (Int32)CTC_CMD_RESP.COM_UART_RX_TEST_DATA_CMD:
                            {
                                UInt16 status = (UInt16)((packetData[4] << 8) + packetData[5]);
                                byte uart_num = packetData[6];
                                int len = (UInt16)((packetData[1] << 8) + packetData[2]);

                                int datalen = len - 3;                               
                                if (datalen>0)
                                {
                                    byte[] data = new byte[datalen];
                                    string msg = Encoding.UTF8.GetString(packetData,7, datalen);
                                    printLog(String.Format("COM{0},{1}", uart_num, msg));
                                }
                            }
                            break;
                        default:
                            {
                                String msg = "";
                                for (int i = 0; i < packetData.Length; i++) 
                                {
                                    msg += String.Format("{0:X2} ", packetData[i]);
                                }
                                this.Invoke(new Action(() =>
                                {
                                    printLog(String.Format("unknown  #{0},message:", msg));
                                    printLog(String.Format("{0}",msg));
                                }));
                            }
                            break;
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

        }

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
#if false
                    if (!DicCan_uhf_map.ContainsKey(ircan_uhf_addr))
                    {
                        uhf_can_tag map = new uhf_can_tag();
                        map.epclist = new List<string>();

                        map.ant_index = ant;
                        //map.can_addr = tag.CanAddr;//配置文件决定
                        map.uhfcan_addr = tag.CanAddr;
                        map.epclist.Add(tag.Epc);

                        map.lastUpdateTime = DateTime.Now.ToLocalTime();
                        DicCan_uhf_map.Add(ircan_uhf_addr, map);
                    }
                    else
                    {
                        uhf_can_tag map = DicCan_uhf_map[ircan_uhf_addr];
                        if (!map.epclist.Contains(tag.Epc))
                            map.epclist.Add(tag.Epc);
                    }
#endif
#if false
                    this.Invoke(new Action(() =>
                    {
                        //0x0100
                        try
                        {
                            int ircanaddr = 0;
                            if (uhfant2IrAddr(tag.CanAddr, tag.Ant, out ircanaddr))
                            {
                                int actual_ir_addr_index = ircanaddr - 0x100;
                                TextBox obj = (TextBox)textbox_control_array[actual_ir_addr_index];
                                uhf_can_tag map = DicCan_uhf_map[ircan_uhf_addr];
                                string tag_str = "";
                                for (int i = 0; i < map.epclist.Count; i++)
                                {
                                    tag_str += map.epclist[i];
                                }

                                obj.Text = tag_str;

                                textbox_control_array[actual_ir_addr_index].BackColor = Color.Green;
                            }
                        }
                        catch (Exception ex)
                        {
                            printLog("配置异常:" + ex.Message);
                        }

                    }));
#endif

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

        /// <summary>
        /// 组装命令
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>

        private bool SendCommand(int addr,byte[] data)
        {
            byte[] sendData = new byte[8 + 5];//fixed:abyte+4byte addr+ 8 bytes

            sendData[0] = (byte)(data.Length&0xf);
            sendData[1] = (byte)((addr >> 24) & 0xff);//address
            sendData[2] = (byte)((addr >> 16) & 0xff);
            sendData[3] = (byte)((addr >> 8) & 0xff);
            sendData[4] = (byte)((addr >> 0) & 0xff);
            Array.Copy(data, 0, sendData, 5, data.Length);

            //sendData[sendData.Length - 1] = Util.Checksum(sendData);

            printLog("【Send   】" + Util.ToHexString(sendData));

            return client.Send(sendData);
        }

        private bool SendCommandExt(int addr, byte[] data)
        {
            byte[] sendData = new byte[8 + 5];//fixed:abyte+4byte addr+ 8 bytes

            sendData[0] = (byte)((data.Length & 0xf)| 0x80) ; //extend frame
            sendData[1] = (byte)((addr >> 24) & 0xff);//address
            sendData[2] = (byte)((addr >> 16) & 0xff);
            sendData[3] = (byte)((addr >> 8) & 0xff);
            sendData[4] = (byte)((addr >> 0) & 0xff);
            Array.Copy(data, 0, sendData, 5, data.Length);

            //sendData[sendData.Length - 1] = Util.Checksum(sendData);

            printLog("【Send   】" + Util.ToHexString(sendData));

            return client.Send(sendData);
        }

        private bool SendCommandExt(int addr, byte[] data, int dataLength)
        {
            if (dataLength > 8 || (data.Length < dataLength))
            {
                printLog($"#{addr.ToString("X3")} SendCommandExt 数据长度错误!");
                return false;
            }
            byte[] sendData = new byte[8 + 5];//fixed:abyte+4byte addr+ 8 bytes
            sendData[0] = (byte)((dataLength & 0xf) | 0x80); //extend frame
            sendData[1] = (byte)((addr >> 24) & 0xff);//address
            sendData[2] = (byte)((addr >> 16) & 0xff);
            sendData[3] = (byte)((addr >> 8) & 0xff);
            sendData[4] = (byte)((addr >> 0) & 0xff);
            Array.Copy(data, 0, sendData, 5, dataLength);

            //sendData[sendData.Length - 1] = Util.Checksum(sendData);

            printLog("【Send   】" + Util.ToHexString(sendData));

            return client.Send(sendData);
        }

        private int CreateExtendID(byte srcID, byte dataType, byte param1, byte devID)
        {
            return ((srcID & 0xFF) << 21) | (dataType << 12) | ((param1 & 0x0F) << 8) | (devID | 0x80);
        }

        private int CreateExtendID(byte srcID, byte dataType, byte param1, byte devID,byte reserved)
        {
            return ((srcID & 0xFF) << 21) | ((reserved & 0x01) << 20) | (dataType << 12) | ((param1 & 0x0F) << 8) | (devID | 0x80);
        }

        /// <summary>
        /// 接收数据包
        /// </summary>
#if false
        private void ReceivePacket()
        {
            pcs = new ProducerConsumerStream();

            while (client.IsAlive())
            {

                byte[] receiveData = client.Receive();             
                if (receiveData != null)
                {
                    //Console.WriteLine(Util.ToHexString(receiveData));
                    pcs.Write(receiveData, 0, receiveData.Length);
                }

                //04 00 00 01 5F FF FF FF 02 00 00 00 00
                if (pcs.DataPosition() < 13)
                {
                    Thread.Sleep(50);
                    continue;
                }

                MemoryStream innerStream = new MemoryStream();

                byte readBytes = (byte)pcs.ReadByte();

                if ((readBytes & 0xf) > 8)
                {
                    printLog("Outer First Packet Header Error,First Header 【" + Convert.ToString(readBytes, 16) + "】");
                    //SDKLog.Error("{0}Outer First Packet Header Error,First Header {1}", reader.LogHeader, readBytes);

                    //if (CustomTraceListener.HasConsole)
                    //Util.PrintCustomTrace("First Packet Header Error");

                    innerStream.Close();
                    continue;
                }
                innerStream.WriteByte(readBytes);
                int packetLen = 12;

                DateTime st = DateTime.Now;


                byte[] uart_receivebuffer = new byte[packetLen];

                pcs.Read(uart_receivebuffer, 0, packetLen);

                innerStream.Write(uart_receivebuffer, 0, packetLen);

                byte[] readData = new byte[innerStream.Length];
                innerStream.Seek(0, SeekOrigin.Begin);
                innerStream.Read(readData, 0, (int)innerStream.Length);

                innerStream.Close();

                //if (SDKLog.LogEnable("IsDebugEnabled"))
                //SDKLog.Debug("{0}[Recv] {1}", reader.LogHeader, Util.ToHexString(readData));

                //if (CustomTraceListener.HasConsole)
                //Util.PrintCustomTrace("[Recv] " + Util.ToHexString(readData));
                printLog("【Receive】" + Util.ToHexString(readData));
                ParsePacket(readData);

                pcs.CopyTo();//将数据重新Copy到新的流中
            }
            pcs.Close();
            pcs = null;
        }
#endif

/// <summary>
/// 解析封包
/// </summary>
/// <param name="packetData"></param>
#if false
        private void ParsePacket(byte[] packetData)
        {
            int dataLength = packetData[0] & 0xF;// 包长度
            int frameType = packetData[0] & 0x80; //帧类型， 0x80:扩展帧；0x00:标准帧
            if (frameType == 0x00)
            {
                int addr = (packetData[1] << 24) | (packetData[2] << 16)
                    | (packetData[3] << 8) | (packetData[4] << 0);
                int cmdcode = (packetData[5] << 8) + packetData[6];
                int hasuhf = 0;
                int deviceName = (addr & 0x7f) + 1;
                int irdeviceIndex = addr & 0x7f;
                switch (cmdcode)
                {
                    case 0xFFFF://broadcast device list response
                        //string deviceId = String.Format("#{0:X}", addr);
                        string deviceId = String.Format("#{0:X}-{1}", addr, (addr & 0x7f) + 1);
                        hasuhf = packetData[7] & 0x01;
                        if (!comboBox_device.Items.Contains(deviceId))
                        {
                            this.Invoke(new Action(() =>
                            {
                                string uhftip = (hasuhf == 1) ? " Has UHF" : " None ";
                                printLog(String.Format("#{0},{1},Model:{2}", deviceName, uhftip, dataLength > 3 ? Enum.GetName(typeof(IR_CanProductModel), packetData[8]): "unknown" ));
                                comboBox_device.Items.Add(deviceId);
                                comboBox_device.Sorted = true;
                                if (comboBox_device.Items.Count == 1)
                                {
                                    comboBox_device.SelectedIndex = 0;
                                }

                            }));
                        }
                        this.Invoke(new Action(() =>
                        {
                            //0x0100
                            Label obj = (Label)label_control_array[addr & 0x7f];
                            if (hasuhf == 1)
                            {
                                Image img = Image.FromFile(@"uhf.png");
                                obj.Image = img.Clone() as Image;
                                obj.Size = img.Size;
                                img.Dispose();
                                obj.ForeColor = Color.OrangeRed;
                            }
                            else
                            {
                                Image img = Image.FromFile(@"status-green.png");
                                obj.Image = img.Clone() as Image;
                                obj.Size = img.Size;
                                img.Dispose();
                            }

                        }));
                        break;
                    case 0x8080:
                        {
                            cmdcode = packetData[7];

                            //int deviceName = (addr + 1) & 0xff;
                            //Console.WriteLine(String.Format("#{0},IR threshhold:{1}", deviceName, ir_threshhold));
                            switch (cmdcode)//set param
                            {
                                case 0x80:

                                    break;
                                case 0x81:
                                    {
                                        UInt16 calval = (UInt16)(packetData[8]);
                                        UInt16 ir_nPercent = (UInt16)(packetData[10] % 100);
                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},IR 校准数据:{1},IR距离参数{2}", deviceName, calval, ir_nPercent));
                                            textBox_irarg.Text = ir_nPercent.ToString();
                                            textBox_calval.Text = calval.ToString();
                                        }));
                                    }

                                    break;
                                case 0x82://write calibration data
                                    {
                                        UInt16 calval = (UInt16)(packetData[8]);
                                        UInt16 ir_nPercent = (UInt16)(packetData[10] % 100);
                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},IR 校准参数:{1}", deviceName, calval));
                                            textBox_irarg.Text = ir_nPercent.ToString();
                                            textBox_calval.Text = calval.ToString();
                                        }));
                                    }

                                    break;
                                case 0x83://write adjust distance data
                                    {
                                        UInt16 calval = (UInt16)(packetData[8]);
                                        UInt16 ir_nPercent = (UInt16)(packetData[10] % 100);
                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},IR 距离参数:{1}", deviceName, ir_nPercent));
                                            textBox_irarg.Text = ir_nPercent.ToString();
                                            textBox_calval.Text = calval.ToString();
                                        }));
                                    }

                                    break;
                                case 0x85:
                                    byte min_H = (packetData[8]);
                                    byte min_M = (packetData[9]);
                                    byte min_L = (packetData[10]);
                                    this.Invoke(new Action(() =>
                                    {
                                        printLog($"#{deviceName},IR Firmware version:V{min_H}.{min_M}.{min_L},Model:{(dataLength >6 ? Enum.GetName(typeof(IR_CanProductModel), packetData[11]):"unknown")}");

                                    }));
                                    break;
                                case 0x86:
                                    byte status = (packetData[8]);

                                    this.Invoke(new Action(() =>
                                    {
                                        printLog(String.Format("#{0},watchdog status:{1}", deviceName, status));

                                    }));
                                    break;
                                case 0x89: //get 阈值上下界
                                    byte thresholdCeiling = (packetData[8]);
                                    byte thresholdFloor = (packetData[9]);
                                    this.Invoke(new Action(() =>
                                    {
                                        printLog(String.Format("#{0}, get threshold Ceiling:{1},Floor:{2}", deviceName, thresholdCeiling, thresholdFloor));
                                        textBox_thresholdCeiling.Text = thresholdCeiling.ToString();
                                        textBox_thresholdFloor.Text = thresholdFloor.ToString();

                                    }));
                                    break;
                                case 0x8A: //set 阈值上下界
                                    byte set_thresholdCeiling = (packetData[8]);
                                    byte set_thresholdFloor = (packetData[9]);

                                    this.Invoke(new Action(() =>
                                    {
                                        printLog(String.Format("#{0}, set threshold Ceiling:{1},Floor:{2}", deviceName, set_thresholdCeiling, set_thresholdFloor));

                                    }));
                                    break;
                                case 0xA0: //inventory cmd responce
                                    {
                                        byte invStatus = (packetData[8]);

                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},Inventory status:{1}", deviceName, invStatus));

                                        }));
                                    }
                                    break;
                                case 0xA2: //Set TxPowerLevel responce
                                    {
                                        byte powerStatus = (packetData[8]);

                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},txPowerLevel:{1}", deviceName, powerStatus));

                                        }));
                                    }
                                    break;
                                case 0xA3: //get TxPowerLevel responce
                                    {
                                        byte powerStatus = (packetData[8]);

                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},get txPowerLevel:{1}", deviceName, powerStatus));
                                            ant1PowerNb.Value = powerStatus;

                                        }));
                                    }
                                    break;
                                case 0xA6: //查看 UHF 是否存在
                                    {
                                        string deviceId_t = String.Format("#{0:X}", addr);
                                        hasuhf = packetData[8];
                                        this.Invoke(new Action(() =>
                                        {
                                            string uhftip = (hasuhf == 1) ? " Has UHF" : " None ";
                                            printLog(String.Format("#{0},{1}", (addr + 1) & 0xff, uhftip));

                                        }));
                                        this.Invoke(new Action(() =>
                                        {
                                            //0x0100
                                            Label obj = (Label)label_control_array[addr & 0x7f];
                                            if (hasuhf == 1)
                                            {
                                                Image img = Image.FromFile(@"uhf.png");
                                                obj.Image = img.Clone() as Image;
                                                obj.Size = img.Size;
                                                img.Dispose();
                                                obj.ForeColor = Color.OrangeRed;
                                            }
                                            else
                                            {
                                                Image img = Image.FromFile(@"status-green.png");
                                                obj.Image = img.Clone() as Image;
                                                obj.Size = img.Size;
                                                img.Dispose();
                                            }

                                        }));
                                        break;
                                    }
                                case 0xAD: //set ir trigger (read LF Tag) 
                                    {
                                        byte ir_triggerSetting = (packetData[8]);

                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},IR trigger LF:{1}", deviceName, ir_triggerSetting !=0 ?"Enable":"Disable"));
                                            comboBox_IRtriggerStatus.SelectedIndex = ir_triggerSetting != 0 ? 0 : 1;
                                            comboBox_IRtriggerStatus.Enabled = true;
                                        }));
                                    }
                                    break;
                                case 0xAE: //get ir trigger status(read LF Tag)
                                    {
                                        byte ir_triggerSetting = (packetData[8]);

                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},IR trigger LF:{1}", deviceName, ir_triggerSetting != 0 ? "Enable" : "Disable"));
                                            comboBox_IRtriggerStatus.SelectedIndex = ir_triggerSetting != 0 ? 0 : 1;
                                            comboBox_IRtriggerStatus.Enabled = true;
                                        }));
                                    }
                                    break;
                                case 0x91: //OLED clear screen
                                    {
                                        this.Invoke(new Action(() =>
                                        {
                                            printLog(String.Format("#{0},Clear OLED Screen!", deviceName));
                                        }));
                                    }
                                    break;
                                default:

                                    break;
                            }
                        }

                        break;
                    case 0xCCCC://uhf tag
                        {
                            //int deviceName = (addr + 1) & 0x7f;
                            //int irdeviceIndex = addr & 0x7f;
                            string epcStr = "";
                            int epcLength = (dataLength - 2);
                            for (int i = 0; i < epcLength; i++)
                            {
                                epcStr += packetData[7 + i].ToString("X2");
                            }
                            this.Invoke(new Action(() =>
                            {
                                //string stamp = DateTime.Now.ToUniversalTime().ToString();
                                //string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                //printLog(String.Format("#{0}-{1} EPC:{2}", deviceName, stamp, epcStr));
                                printLog(String.Format("#{0} EPC:{2}", deviceName, epcStr));
                                //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                            }));
                        }
                        break;
                    case 0x6666://uhf tag parted
                        {
                            string epcStr = "";
                            int epcPartLength = (dataLength - 4);
                            int tag_sn = packetData[7];
                            int epc_length = ((packetData[8] & 0xF0) >> 3);
                            int epc_parted_sn = (packetData[8] & 0x0F);
                            for (int i = 0; i < epcPartLength; i++)
                            {
                                epcStr += packetData[9 + i].ToString("X2");
                            }
                            //string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                            this.Invoke(new Action(() =>
                            {

                                //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} EPC part{4}:{5}", deviceName, stamp, tag_sn, epc_length, epc_parted_sn, epcStr));
                                printLog(String.Format("#{0} SN:{1} EPC Length:{2} EPC part{3}:{4}", deviceName, tag_sn, epc_length, epc_parted_sn, epcStr));
                                //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                            }));
                            string tempEpcSrc = TagEPCsegments.CreateEPCSource(addr, tag_sn, epc_length);
                            if (DictTagEPCsegments.ContainsKey(tempEpcSrc))
                            {
                                DictTagEPCsegments[tempEpcSrc].AddepcSegment(addr, tag_sn, epc_length, epcStr, epc_parted_sn);

                                if (DictTagEPCsegments[tempEpcSrc].IsEPCFull)
                                {
                                    //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} full EPC:{4}", deviceName, stamp, tag_sn, epc_length, DictTagEPCsegments[tempEpcSrc].GetFullEPC()));
                                    printLog(String.Format("#{0} SN:{1} EPC Length:{2} full EPC:{3}", deviceName,tag_sn, epc_length, DictTagEPCsegments[tempEpcSrc].GetFullEPC()));
                                }
                            }
                            else
                            {
                                TagEPCsegments temptagEPCsegments = new TagEPCsegments(addr, tag_sn, epc_length, epcStr, epc_parted_sn);
                                DictTagEPCsegments.Add(temptagEPCsegments.EpcSource, temptagEPCsegments);
                                if (temptagEPCsegments.IsEPCFull)
                                {
                                    //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} full EPC:{4}", deviceName, stamp, tag_sn, epc_length, temptagEPCsegments.GetFullEPC()));
                                    printLog(String.Format("#{0} SN:{1} EPC Length:{2} full EPC:{3}", deviceName,tag_sn, epc_length, temptagEPCsegments.GetFullEPC()));
                                }
                            }
                        }
                        break;
                    case 0x7777://uhf tag rssi //20220324 added
                        {
                            if (dataLength == 8)
                            {
                                int tag_sn = packetData[7];
                                int epc_length = (packetData[8] * 2); //bytes
                                byte[] rssi_data = new byte[4] { packetData[9], packetData[10], packetData[11], packetData[12] };
                                string rssi_str = CalcTagRssi(rssi_data);

                                //string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                this.Invoke(new Action(() =>
                                {
                                    //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} RSSI:{4}", deviceName, stamp, tag_sn, epc_length, rssi_str));
                                    printLog(String.Format("#{0} SN:{1} EPC Length:{2} RSSI:{3}", deviceName, tag_sn, epc_length, rssi_str));
                                    //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                                }));
                                //DictTagEPCsegments.
                                string tempEpcSrc = TagEPCsegments.CreateEPCSource(addr, tag_sn, epc_length);
                                if (DictTagEPCsegments.ContainsKey(tempEpcSrc))
                                {
                                    DictTagEPCsegments[tempEpcSrc].AddTagRSSI(tempEpcSrc, rssi_str);

                                    if (DictTagEPCsegments[tempEpcSrc].IsEPCFullwithRSSI)
                                    {
                                        //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} full EPC:{4} RSSI:{5}",
                                        //         deviceName, stamp, tag_sn, epc_length,
                                        //         DictTagEPCsegments[tempEpcSrc].GetFullEPC(),
                                        //         DictTagEPCsegments[tempEpcSrc].RSSI));
                                        printLog(String.Format("#{0} SN:{1} EPC Length:{2} full EPC:{3} RSSI:{4}",
                                                 deviceName, tag_sn, epc_length,
                                                 DictTagEPCsegments[tempEpcSrc].GetFullEPC(),
                                                 DictTagEPCsegments[tempEpcSrc].RSSI));

                                    }
                                }
                                else
                                {
                                    //printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} unknown Tag RSSI{4}", deviceName, stamp, tag_sn, epc_length, rssi_str));
                                    printLog(String.Format("#{0} SN:{1} EPC Length:{2} unknown Tag RSSI{3}", deviceName,tag_sn, epc_length, rssi_str));
                                }
                            }
                            else
                            {
                                this.Invoke(new Action(() =>
                                {
                                    //string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                    //printLog(String.Format("#TAG-RSSI invalid:{0}-{1} lengh:{2}", deviceName, stamp, dataLength));
                                    printLog(String.Format("#TAG-RSSI invalid:{0} lengh:{1}", deviceName, dataLength));
                                    //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                                }));
                            }
                        }
                        break;
                    case 0x6767://uhf tag rssi //20220417 added
                        {
                            if (dataLength >= 6)
                            {
                                int tag_sn = packetData[7];
                                int epc_length = (packetData[8] * 2); //bytes
                                UInt32 rssi_H = packetData[9];
                                UInt32 rssi_L = packetData[10];
                                UInt32 rssi_data = ((rssi_H << 8) | (rssi_L));
                                //Console.WriteLine("rssi:{0:X}", rssi_data);
                                double rssi_f = rssi_data / 10.0;
                                string rssi_str = String.Format("-{0:0.0}", rssi_f);

                                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                this.Invoke(new Action(() =>
                                {
                                    printLog(String.Format("#{0}-{1} SN:{2} EPC Length:{3} RSSI:{4}", deviceName, stamp, tag_sn, epc_length, rssi_str));
                                    //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                                }));
                                //DictTagEPCsegments.
                                string tempEpcSrc = TagEPCsegments.CreateEPCSource(addr, tag_sn, epc_length);
                                if (DictTagEPCsegments.ContainsKey(tempEpcSrc))
                                {
                                    DictTagEPCsegments[tempEpcSrc].AddTagRSSI(tempEpcSrc, rssi_str);

                                    if (DictTagEPCsegments[tempEpcSrc].IsEPCFullwithRSSI)
                                    {
                                        printLog(String.Format("#{0}-{1} SN:{2} EPC Len:{3} fullEPC:{4} RSSI:{5}",
                                                 deviceName, stamp, tag_sn, epc_length,
                                                 DictTagEPCsegments[tempEpcSrc].GetFullEPC(),
                                                 DictTagEPCsegments[tempEpcSrc].RSSI));
                                    }
                                }
                                else
                                {
                                    printLog(String.Format("#{0}-{1} SN:{2} EPC Len:{3} unknown Tag RSSI{4}", deviceName, stamp, tag_sn, epc_length, rssi_str));
                                }
                            }
                            else
                            {
                                this.Invoke(new Action(() =>
                                {
                                    string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                    printLog(String.Format("#TAG-RSSI invalid:{0}-{1} lengh:{2}", deviceName, stamp, dataLength));
                                    //printLog(String.Format("#TAG-{0}:{1:X2}-{2:X2}-{3:X2}-{4:X2}", stamp, packetData[7], packetData[8], packetData[9], packetData[10]));
                                }));
                            }
                        }
                        break;
                    case 0xDDDD://get IR status ,inactive
                        {
                            Int32 adc = (Int32)(packetData[7] << 8) + packetData[8];
                            //int  deviceName=(addr + 1) & 0x7f;
                            //int irdeviceIndex = addr & 0x7f;
                            if (adc > 0)//关闭
                            {
                                DicDevStatus[irdeviceIndex] = 0;
                            }
                            else//打开
                                DicDevStatus[irdeviceIndex] = 1;

                            switch (DicDevStatus[irdeviceIndex])
                            {
                                case 0://关闭
                                    this.Invoke(new Action(() =>
                                    {
                                        Label obj_off = (Label)label_control_array[irdeviceIndex];
                                        Image img_off = Image.FromFile(@"status-black.png");
                                        obj_off.Image = img_off.Clone() as Image;
                                        obj_off.Size = img_off.Size;
                                        img_off.Dispose();
                                    }));
                                    break;
                                case 1://打开
                                    this.Invoke(new Action(() =>
                                    {
                                        Label obj_on = (Label)label_control_array[irdeviceIndex];
                                        Image img_on = Image.FromFile(@"status-red.png");
                                        obj_on.Image = img_on.Clone() as Image;
                                        obj_on.Size = img_on.Size;
                                        img_on.Dispose();
                                    }));

                                    break;
                                default:

                                    break;
                            }

                            Console.WriteLine(String.Format("#{0},IR:{1}", deviceName, adc));
                            if (bStartCal)
                            {
                                if (caldatalist.Count <= calMax)
                                    caldatalist.Add(adc);
                                else
                                    Console.WriteLine("Have calMax=4 data");
                            }

                            this.Invoke(new Action(() =>
                            {
                                printLog(String.Format("Inactive #{0},IR:{1}", deviceName, adc));
                            }));
                        }

                        break;
                    case 0xDEDE://get IR status,active
                        {
                            Int32 adc = (Int32)(packetData[7] << 8) + packetData[8];
                            //int deviceName = (addr + 1) & 0x7f;
                            //int irdeviceIndex = addr & 0x7f;
                            if (adc > 0)//关闭
                            {
                                DicDevStatus[irdeviceIndex] = 0;
                            }
                            else//打开
                                DicDevStatus[irdeviceIndex] = 1;

                            switch (DicDevStatus[irdeviceIndex])
                            {
                                case 0://关闭
                                    this.Invoke(new Action(() =>
                                    {
                                        Label obj_off = (Label)label_control_array[irdeviceIndex];
                                        Image img_off = Image.FromFile(@"status-black.png");
                                        obj_off.Image = img_off.Clone() as Image;
                                        obj_off.Size = img_off.Size;
                                        img_off.Dispose();
                                    }));
                                    break;
                                case 1://打开
                                    this.Invoke(new Action(() =>
                                    {
                                        Label obj_on = (Label)label_control_array[irdeviceIndex];
                                        Image img_on = Image.FromFile(@"status-red.png");
                                        obj_on.Image = img_on.Clone() as Image;
                                        obj_on.Size = img_on.Size;
                                        img_on.Dispose();
                                    }));

                                    break;
                                default:

                                    break;
                            }

                            Console.WriteLine(String.Format("#{0},IR:{1}", deviceName, adc));
                            if (bStartCal)
                            {
                                if (caldatalist.Count <= calMax)
                                    caldatalist.Add(adc);
                                else
                                    Console.WriteLine("Have calMax=4 data");
                            }

                            this.Invoke(new Action(() =>
                            {
                                printLog(String.Format("Active #{0},IR:{1}", deviceName, adc));
                            }));
                        }

                        break;
                    case 0xEEEE://Error code
                        {
                            //int deviceName = (addr + 1) & 0x7f;
                            int errcode = (Int32)(packetData[7] << 8) + packetData[8]; ;
                            Console.WriteLine(String.Format("Err #{0},IR:{1:X}", deviceName, errcode));

                            this.Invoke(new Action(() =>
                            {
                                if (errcode == 0xff02)
                                {
                                    Label obj = (Label)label_control_array[addr & 0x7f];
                                    Image img = Image.FromFile(@"status-yellow.png");
                                    obj.Image = img.Clone() as Image;
                                    obj.Size = img.Size;
                                    img.Dispose();
                                    printLog(String.Format("Err #{0},IR:{1:X}", deviceName, errcode));
                                }
                                else if (errcode == 0xff10)//watchdog reset
                                {
                                    Label obj = (Label)label_control_array[addr & 0x7f];
                                    Image img = Image.FromFile(@"status-yellow.png");
                                    obj.Image = img.Clone() as Image;
                                    obj.Size = img.Size;
                                    img.Dispose();
                                    printLog(String.Format("Err watchdog reset {0},IR:{1:X}", deviceName, errcode));
                                }
                            }));
                        }
                        break;
                    case 0x80://读数据

                        crossThreadUpdateUI updateUI = delegate ()
                        {

                        };
                        this.connectBtn.Invoke(updateUI);
                        break;
                    default:
                        break;
                }
            }
            else  //扩展帧
            {
                int extAddr = (packetData[1] << 24) | (packetData[2] << 16)
                            | (packetData[3] << 8) | (packetData[4] << 0);
                int cmdcode = (extAddr >>12) & 0xFF;
                int param1 = (extAddr >> 8) & 0xF;
                int param2 = extAddr & 0xFF;
                int deviceName = ((extAddr>>21) & 0x7f) + 1;
                int irdeviceIndex = (extAddr >> 21) & 0x7f;
                byte[] dataLoad = new byte[dataLength];
                Array.Copy(packetData, 5, dataLoad, 0, dataLength);
                string hexdata;
                string tmp_LF_result = "";
                switch (cmdcode)
                {                 
                    case ExtendIDConst.LF_DT_PAGE_DATA:
                        LF_SuccessTimes++;
                        hexdata = Util.ToHexString(dataLoad);
                        tmp_LF_result = $"#{deviceName},page{param2}:{hexdata}";
                        this.Invoke(new Action(() => { this.textBox_rwData.Text = hexdata; }));
                        printLog(tmp_LF_result);
                        LF_readResult = tmp_LF_result;
                        bReadLFPageSuccess = true;
                        LF_RW_Ret_Done.Set();
                        //Console.Beep(100, 50);
                        break;
                    case ExtendIDConst.LF_DT_IR_EVENT:
                        printLog($"#{deviceName},IR event {(param2 == ExtendIDConst.LF_P2_IR_EVENT_ARRIVE ? "arrive" : "leave")}");
                        break;
                    case ExtendIDConst.LF_DT_READ_NONE:
                        tmp_LF_result = $"#{deviceName},read none!";
                        printLog(tmp_LF_result);
                        LF_readResult = tmp_LF_result;
                        bReadLFPageSuccess = false;
                        LF_RW_Ret_Done.Set();
                        break;
                    case ExtendIDConst.LF_DT_CRC_ERR:
                        string param1_desp = ""; 
                        switch (param1)
                        {
                            case ExtendIDConst.LF_P1_PAGE_CRC:
                                param1_desp = "page CRC";
                                break;
                            case ExtendIDConst.LF_P1_FRAME_CRC:
                                param1_desp = "frame CRC";
                                break;
                            case ExtendIDConst.LF_P1_HEAD_ERR:
                                param1_desp = "head";
                                break;
                            default:
                                param1_desp = "unknown";
                                break;
                        }
                        tmp_LF_result = $"#{deviceName},{param1_desp} ERR!";
                        printLog(tmp_LF_result);
                        LF_readResult = tmp_LF_result;
                        bReadLFPageSuccess = false;
                        LF_RW_Ret_Done.Set();
                        break;
                    case ExtendIDConst.LF_DT_WRITE:
                        printLog($"#{deviceName},write LF {(param2 == ExtendIDConst.LF_P2_WRITE_PAGE_OVER ? "OVER" : "param ERR")}");
                        break;
                    case ExtendIDConst.OLED_DT_WRITE_LINE:
                        printLog($"#{deviceName},OLED display Line {(param2 == ExtendIDConst.OLED_P2_WRITE_LINE_OVER ? "OVER" : "param ERR")}");
                        break;
                    default:
                        printLog($"#{deviceName},{cmdcode:X} unhandled!");
                        break;
                }
            }
        }
#endif
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
            serialCb.Enabled = serialRb.Checked;
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

            //语言设置
            //if (IsChineseTW())
            //{
            //    Setlanguage("Zh-TW");
            //}
            //else if (IsChineseSimple())
            //{
            //    Setlanguage("zh-CN");
            //}
            //else if (IsEnglish())
            //{
            //    Setlanguage("Zh-TW");
            //}
            //else //default
            //{
            //    Setlanguage("Zh-CN");
            //}
            this.Text = $"LFR1M SDK Ver{Get_SDK_Version()}- APP Version 20251229.00";
            

            //Type type2 = dataGridViewIRDevList.GetType();
            //PropertyInfo pi2 = type.GetProperty("DoubleBuffered",
            //    BindingFlags.Instance | BindingFlags.NonPublic);
            //pi2.SetValue(dataGridViewIRDevList, true, null);
            //dataGridViewIRDevList.AutoGenerateColumns = false;

            //bIrDataProcess = true;
            //Thread ir_process_thread = new Thread(IRDataprocess);
            //ir_process_thread.IsBackground = true;
            //ir_process_thread.Start();

            //btagQThreadExit = false;
            //tagQThread = new Thread(new ThreadStart(tagQ_process));
            //tagQThread.Start();
            //uiComboBox_filter_bank.SelectedIndex = 0;
            //uiComboBox_lf_select.SelectedIndex = 0;
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

/// <summary>
/// 收到LF TAG 信息，更新OLED显示
/// </summary>
/// <param name="i"></param>
#if false
        private void UpdateOLEDShow4Tag(object i)
        {
            LFTag lfTag = (LFTag)i;
            List<DisplayInfo> disInfoList = new List<DisplayInfo>();
            Result_t result = Result_t.OK;
            //int device_code = lfTag.IrDevAddr + 1;
            int addr = lfTag.IrDevAddr + 0x100;
            switch (lfTag.LF_Event)
            {
                case LF_EVENT.LF_EVENT_READ_OK:
                    if (bEN_OLED_ACTION)
                    {
                        result = irdevice.OLED_ClearScreen((ushort)(lfTag.IrDevAddr + 0x100));
                        if (result == Result_t.OK)
                        {
                            printLog($"#{addr.ToString("X3")},OLED Clear Screen OVER");
                        }
                        else
                        {
                            printLog(String.Format("#{0},OLED Clear Screen Fail :{1}", addr.ToString("X3"), result));
                        }
                        disInfoList.Add(new DisplayInfo(1, "ABP08P1828931.01.01"));
                        disInfoList.Add(new DisplayInfo(2, "WFR INSPECION 5 FOR WLCSP"));
                        disInfoList.Add(new DisplayInfo(3, "C3BU_1" + Encoding.ASCII.GetString(Util.ToHexByte(lfTag.PageHexData))));
                        result = irdevice.Set_Display_Info(lfTag.IrDevAddr, disInfoList);
                        if (result == Result_t.OK)
                        {
                            printLog($"#{addr.ToString("X3")},OLED Display_Info OVER");
                        }
                        else
                        {
                            printLog(String.Format("#{0},OLED Display_Info Fail :{1}", addr.ToString("X3"), result));
                        }
                    }
                    break;
                case LF_EVENT.LF_EVENT_READ_NONE:
                case LF_EVENT.LF_EVENT_PAGE_CRC_ERR:
                case LF_EVENT.LF_EVENT_FRAME_CRC_ERR:
                case LF_EVENT.LF_EVENT_HEAD_ERR:
                    if (bEN_OLED_ACTION)
                    {
                        result = irdevice.OLED_ClearScreen((ushort)(lfTag.IrDevAddr + 0x100));
                        //device_code = lfTag.IrDevAddr + 1;
                        if (result == Result_t.OK)
                        {
                            printLog($"#{addr.ToString("X3")},OLED Clear Screen OVER");
                        }
                        else
                        {
                            printLog(String.Format("#{0},OLED Clear Screen Fail :{1}", addr.ToString("X3"), result));
                        }
                        disInfoList.Add(new DisplayInfo(1, "Read None!"));
                        result = irdevice.Set_Display_Info(lfTag.IrDevAddr, disInfoList);
                        if (result == Result_t.OK)
                        {
                            printLog($"#{addr.ToString("X3")},OLED Display_Info OVER");
                        }
                        else
                        {
                            printLog(String.Format("#{0},OLED Display_Info Fail :{1}", addr.ToString("X3"), result));
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// IR 感应到 目标离开
        /// </summary>
        /// <param name="i"></param>
        private void UpdateOLEDShow4IR(object i)
        {
            int irdeviceIndex = (int)i;
            List<DisplayInfo> disInfoList = new List<DisplayInfo>();
            Result_t result = Result_t.OK;
            //int device_code = irdeviceIndex + 1;
            UInt16 addr = (ushort)(irdeviceIndex + 0x100); 

            result = irdevice.OLED_ClearScreen((ushort)(irdeviceIndex + 0x100));
            
            if (result == Result_t.OK)
            {
                printLog($"#{addr.ToString("X3")},OLED Clear Screen OVER");
            }
            else
            {
                printLog(String.Format("#{0},OLED Clear Screen Fail :{1}", addr.ToString("X3"), result));
            }
            disInfoList.Add(new DisplayInfo(1, "NO FOUP!"));
            result = irdevice.Set_Display_Info((ushort)irdeviceIndex, disInfoList);
            if (result == Result_t.OK)
            {
                printLog($"#{addr.ToString("X3")},OLED Display_Info OVER");
            }
            else
            {
                printLog(String.Format("#{0},OLED Display_Info Fail :{1}", addr.ToString("X3"), result));
            }         
        }

        /// <summary>
        /// IR 触发读取 LF Tag
        /// </summary>
        /// <param name="i"></param>
        private void ReadLFTag4IRTrigger(object i)
        {
            int addr  = (int)i;
            Result_t result = Result_t.OK;
            lock (ir_trigger_lock)
            {
                Thread.Sleep(300);
                result = irdevice.LF_ReadTag((ushort)addr, pageNum);
                Thread.Sleep(200);
            }
        }
#endif
        private void disconnectBtn_Click(object sender, EventArgs e)
        {
#if false
            if (client != null)
            {

                Thread.Sleep(200);
                //最后关闭连接
                client.DisConnect();
            }
            this.connectBtn.Enabled = true;
            this.disconnectBtn.Enabled = false;
            this.mainPanel.Enabled = false;
#else
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
#endif
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
            //String selDev = "";
            //UInt16 addr = 0;
            //if (comboBox_device.SelectedIndex == -1)
            //{
            //    MessageBox.Show("请选择设备!");
            //    return;
            //}
            //selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            ////DictTagEPCsegments.Clear(); //开始新的盘点前，先清除DictTagEPCsegments中的数据
            //byte ant = Convert.ToByte(textBox_ant.Text);
            //byte time = byte.Parse(textBox_invtine.Text);

            //addr = Convert.ToUInt16(selDev, 16);

            //int hasuhf = 0;
            //int device_code = (addr & 0x7f) + 1;
            //Result_t result = irdevice.RFID_Check(addr, out hasuhf);
            //if (result == Result_t.OK)
            //{
            //    if (hasuhf == 0)
            //    {
            //        printLog(string.Format("编号{0}，UHF模块不存在", device_code));
            //        return;
            //    }
            //}
            //else
            //{
            //    Console.WriteLine(string.Format("检查UHF设备异常，{0}", result));
            //    return;
            //}
            //result = irdevice.RFID_Inventory(addr, ant, time);

            //button_inv.Enabled = false;
            //if (result == Result_t.OK)
            //{
            //    printLog(String.Format("#{0},盘点中...", device_code));
            //}
            //else if (result == Result_t.ERR_UHF_NOT_EXIST)
            //{
            //    printLog(String.Format("#{0},UHF 不存在", device_code));
            //}
            //else if (result == Result_t.ERR_UHF_INVENTORY_ING)
            //{
            //    printLog(String.Format("#{0},UHF 已经在盘点", device_code));
            //}
            //else
            //{
            //    printLog(String.Format("#{0},盘点启动异常,{1}", device_code, result));
            //}
            //button_inv.Enabled = true;
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
            //int antPower = (int)ant1PowerNb.Value;

            //String selDev = "";
            //UInt16 addr = 0;
            //if (comboBox_device.SelectedIndex == -1)
            //{
            //    MessageBox.Show("请选择设备!");
            //    return;
            //}
            //setAntPowerBtn.Enabled = false;
            //selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            //addr = Convert.ToUInt16(selDev, 16);
            //Result_t result = irdevice.RFID_Set_Tx_power(addr, antPower);
            //int device_code = (addr & 0x7f) + 1;
            //if (result == Result_t.OK)
            //{
            //    printLog(String.Format("#{0},设置功率成功:{1}", device_code, antPower));
            //}
            //else
            //{
            //    printLog(String.Format("#{0},设置功率失败 :{1}", device_code, result));
            //}
            //setAntPowerBtn.Enabled = true;
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
            //String selDev = "";
            //UInt16 addr = 0;
            //if (comboBox_device.SelectedIndex == -1)
            //{
            //    MessageBox.Show("请选择设备!");
            //    return;
            //}
            //getAntPowerBtn.Enabled = false;
            //selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            //addr = Convert.ToUInt16(selDev, 16);
            //byte txpower = 0;
            //Result_t result = irdevice.RFID_Get_Tx_power(addr, out txpower);
            //int device_code = (addr & 0x7f) + 1;

            //if (result == Result_t.OK)
            //{
            //    printLog(String.Format("#{0},获取功率成功:{1}", device_code, txpower));
            //    ant1PowerNb.Value = txpower;
            //}
            //else
            //{
            //    printLog(String.Format("#{0},获取功率失败 :{1}", device_code, result));
            //}
            //getAntPowerBtn.Enabled = true;
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
            //String selDev = "";
            //UInt16 addr = 0;

            //if (comboBox_device.SelectedIndex == -1)
            //{
            //    MessageBox.Show("请选择设备!");
            //    return;
            //}
            //button_uhfCheck.Enabled = false;
            //int hasuhf = 0;
            //Result_t result = Result_t.ERR_FAILED;
            //int device_code = 0;
            //selDev = comboBox_device.Text.Trim('#').Split('-')[0];

            //addr = Convert.ToUInt16(selDev, 16);

            //result = irdevice.RFID_Check(addr, out hasuhf); ;
            //device_code = (addr & 0x7f) + 1;
            //if (result == Result_t.OK)
            //{
            //    string deviceId_t = String.Format("#{0:X}", addr);
            //    this.Invoke(new Action(() =>
            //    {
            //        string uhftip = (hasuhf == 1) ? " Has UHF" : " None UHF";
            //        printLog(String.Format("#{0},{1}", (addr + 1) & 0xff, uhftip));

            //    }));
            //    //this.Invoke(new Action(() =>
            //    //{
            //    //    //0x0100
            //    //    Label obj = (Label)label_control_array[addr & 0x7f];
            //    //    if (hasuhf == 1)
            //    //    {
            //    //        Image img = Image.FromFile(@"uhf.png");
            //    //        obj.Image = img.Clone() as Image;
            //    //        obj.Size = img.Size;
            //    //        img.Dispose();
            //    //        obj.ForeColor = Color.OrangeRed;
            //    //    }
            //    //    else
            //    //    {
            //    //        Image img = Image.FromFile(@"status-green.png");
            //    //        obj.Image = img.Clone() as Image;
            //    //        obj.Size = img.Size;
            //    //        img.Dispose();
            //    //    }

            //    //}));
            //}
            //else
            //{
            //    printLog(String.Format("#{0},检查UHF失败 :{1}", device_code, result));
            //}

            //button_uhfCheck.Enabled = true;
#endif
        }

        static void SaveLog2File(string text)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SaveLog");//生成目录路径
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);//创建目录
            }
            string str2 = Path.Combine(path, "LF-" + DateTime.Today.ToString("yyyy-MM-dd") + ".txt");//生成文件路径           
            try
            {
                using (StreamWriter writer = File.AppendText(str2))
                {
                    writer.Write(text);
                    writer.Flush();
                    writer.Close();
                }
            }
            catch (IOException)
            {
            }
        }

        private void toolStripMenuItemClear_Click(object sender, EventArgs e)
        {
            //MessageBox.Show(sender.ToString());
            this.uicommandListBox.Items.Clear();
        }

#if false
        private void button_getIRtrigger_Click(object sender, EventArgs e)
        {
            String selDev = "";
            UInt16 addr = 0;
            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a device!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            addr = Convert.ToUInt16(selDev, 16);
            Result_t result = irdevice.LF_GetIrTrigger(addr, out bool irTriggerLF);
            //int device_code = (addr & 0x7f) + 1;
            if (result == Result_t.OK)
            {
                comboBox_searchMode.SelectedIndex = irTriggerLF == true ? 0 : 1;
                comboBox_searchMode.Enabled = true;
                printLog(String.Format("#{0},Get LF IR trigger:{1}", addr.ToString("X3"), irTriggerLF));
            }
            else
            {
                printLog(String.Format("#{0},Get LF IR trigger Fail :{1}", addr.ToString("X3"), result));
            }
        }

        private void button_setIRtrigger_Click(object sender, EventArgs e)
        {
            String selDev = "";
            UInt16 addr = 0;
            if (comboBox_device.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a device!");
                return;
            }
            selDev = comboBox_device.Text.Trim('#').Split('-')[0];
            addr = Convert.ToUInt16(selDev, 16);
            Result_t result = irdevice.LF_SetIrTrigger(addr, comboBox_searchMode.SelectedIndex == 0 ? true : false);
            //int device_code = (addr & 0x7f) + 1;
            if (result == Result_t.OK)
            {
                printLog(String.Format("#{0},LF IR trigger setting OK", addr.ToString("X3")));
            }
            else
            {
                printLog(String.Format("#{0},LF IR trigger setting Fail :{1}", addr.ToString("X3"), result));
            }
        }
#endif
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




        //当前操作系统是否为简体中文
        public static bool IsChineseSimple()
        {
            return Thread.CurrentThread.CurrentCulture.Name == "zh-CN";
        }

        //当前操作系统是否为繁体中文
        public static bool IsChineseTW()
        {
            return Thread.CurrentThread.CurrentCulture.Name == "Zh-TW";
        }

        //当前操作系统是否为英语（美国）
        public static bool IsEnglish()
        {
            return Thread.CurrentThread.CurrentCulture.Name == "en-US";
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


        private void tagQ_process()
        {
            while (btagQThreadExit == false)
            {
                if (tagDataQ.Count > 0)
                {
                    TagData tagdata;
                    //string hexdata;
                    lock (tagDataQ)
                    {
                        tagdata = tagDataQ.Dequeue();
                    }
                    getTagsCount++;
#if true
                    this.Invoke(new Action(() =>
                    {
                        //this.uiTextBox_rwData.Text = !uiCheckBox_rwTextHex.Checked ? Encoding.ASCII.GetString(Util.ToHexByte(hexdata)) : hexdata;
                        DataRow[] dataRows = tagdataTable.Select($"EPC = '{(this.bEPC_ASCII ? Encoding.ASCII.GetString(Util.ToHexByte(tagdata.Epc)) : tagdata.Epc)}' AND Addr = '{tagdata.CanAddr.ToString("X3")}'");
                        if (dataRows.Length > 0)
                        {
                            dataRows[0]["EPC"] = this.bEPC_ASCII ? Encoding.ASCII.GetString(Util.ToHexByte(tagdata.Epc)) : tagdata.Epc;
                            dataRows[0]["Count"] = (int)dataRows[0]["Count"]+1;
                            dataRows[0]["UpdateTime"] = DateTime.Now.ToString("yy-MM-dd HH:mm:ss.fff");
                            dataRows[0]["IP"] = tagdata.Ip;//
                        }
                        else
                        {
                            DataRow dataRow = tagdataTable.NewRow();
                            dataRow["Addr"] = tagdata.CanAddr.ToString("X3");
                            dataRow["Line No."] = tagdataTable.Rows.Count+1;
                            dataRow["Count"] = 1;
                            dataRow["TAG_SN"] = tagdata.Tag_sn;//tagdata.Tag_sn.ToString();          
                            dataRow["EPC"] = this.bEPC_ASCII ? Encoding.ASCII.GetString(Util.ToHexByte(tagdata.Epc)) : tagdata.Epc;
                            dataRow["UpdateTime"] = DateTime.Now.ToString("yy-MM-dd HH:mm:ss.fff");
                            dataRow["IP"] = tagdata.Ip;//
                            //Console.WriteLine($"ip:{tagdata.Ip}");
                            tagdataTable.Rows.Add(dataRow);  //必须在Invoke里，否则dataGridViewIRDevList出现滚动条后，UI容易hang
                            //uiDataGridView_IRDevList.DataSource = tagdataTable;
                        }
                        
                    }));
#endif
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }



        private void uiButton_lf_stop_Click(object sender, EventArgs e)
        {
            bLF_TestRunning=false;
            uiButton_lf_read.Enabled = true;
        }
        private void uiButton_lf_read_Click(object sender, EventArgs e)
        {
            int index = uiComboBox_lf_select.SelectedIndex;
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
                        break;
                    }

                    for(int p = 0; p < 6; p++)
                    {
                        ret = irdevice.CTC_Read_LF(index, 1, 1, out lfdada);
                        if (ret == Result_t.OK)
                            break;

                        //Thread.Sleep(10);
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

                        printLog(String.Format("read lf lfdada:{0}", lfdada));
                        Console.Beep();
                    }
                    else
                    {
                        printLog(String.Format("read LF failed，ERROR,{0}", ret));
                        failed_count++;
                    }
                    Thread.Sleep(20);
                    this.Invoke(new Action(() =>
                    {
                        uiLabel_succ_count.Text = succ_count.ToString();
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
                if (irdevice.gpio_get_gpi(1, out value)==Result_t.OK)
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
                if (irdevice.gpio_get_gpi(2, out value) == Result_t.OK)
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
                if (irdevice.gpio_get_gpi(3, out value) == Result_t.OK)
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
                if (irdevice.gpio_get_gpi(4, out value) == Result_t.OK)
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
    }

}

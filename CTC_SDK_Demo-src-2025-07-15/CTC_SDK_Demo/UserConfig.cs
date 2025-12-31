using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CommandDemo
{
    [Serializable]  //表示这个类可以被序列化
    class UserConfig  //
    {

        public bool IsEPC_HEX { get; set; }

        public UserConfig(bool bEPC_HEX)  //构造函数
        {
            this.IsEPC_HEX = bEPC_HEX;
        }
    }
}

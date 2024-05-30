using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json; 
using Newtonsoft.Json.Linq;
using RestSharp;

namespace FireAlarmSystem
{
    public partial class FireAlarmForm : Form
    {
        public FireAlarmForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 烟雾浓度属性上传
        /// 官方文档：https://open.iot.10086.cn/doc/iot_platform/book/device-connect&manager/HTTP/api/devicePropertyNotify.html
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void bt_UploadProperty_Click(object sender, EventArgs e)
        {
            string productId = tb_ProductId.Text.Trim();
            string deviceName = tb_DeviceName.Text.Trim();
            string deviceKey = tb_DeviceKey.Text.Trim();
            double smoke = double.Parse(tb_Smoke.Text.Trim());

            JObject payload = GenSmokeProperty(smoke);
            UploadProperty(productId, deviceName, deviceKey, payload);
        }

        /// <summary>
        /// 火情检测
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void bt_FireDetect_Click(object sender, EventArgs e)
        {
            // 创建文件选择对话框
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Title = "请选择上传的图片";
            ofd.Filter = "图片格式|*.jpg";
            ofd.Multiselect = false;

            string fileFullName = null;
            if (ofd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            // 获得文件的完整名称，包括路径和文件名
            fileFullName = ofd.FileName;

            // 第一步：上传文件到AI平台，并获得火情检验的结果
            string aiKey = tb_AiKey.Text.ToString();
            string secretKey = tb_AiSecret.Text.ToString();
            string label = null;
            double confidence = 0;
            if (!FireDetect(fileFullName, aiKey, secretKey, ref label, ref confidence))
                return;

            // 第二步：将该文件上传到OneNET Studio平台进行保存，以便追溯
            string productId = tb_ProductId.Text.Trim();
            string deviceName = tb_DeviceName.Text.Trim();
            string deviceKey = tb_DeviceKey.Text.Trim();
            string uuid = UploadFile(fileFullName, productId, deviceName, deviceKey);

            // 第三步：将识别结果和文件uuid作为属性上传OneNET Studio
            JObject payload = GenAiRecordProperty(uuid, label, confidence);
            UploadProperty(productId, deviceName, deviceKey, payload);
        }

 
        /// <summary>
        /// 上传图片到AI平台，并完成火情检测
        /// 官方文档：https://open.iot.10086.cn/ai/helpCenter/technicalDoc?id=c6
        /// </summary>
        /// <param name="fileFullName">文件全名</param>
        /// <param name="aiKey">AI Key</param>
        /// <param name="secretKey">Secret Key</param>
        /// <param name="label">AI识别的标签</param>
        /// <param name="confidence">置信度</param>
        /// <returns></returns>
        private bool FireDetect(string fileFullName, string aiKey, string secretKey, ref string label, ref double confidence)
        {
            string url = string.Format("http://ai.heclouds.com:9090/v1/aiApi/picture/FIRE_DETECTION");

            string token = AiPlatform.GetAccessToken(aiKey, secretKey);
            //MessageBox.Show(token);

            JObject payload = GenFireDetectionPayload(fileFullName);

            try
            {
                var client = new RestClient(url);
                client.Timeout = -1;
                var request = new RestRequest(Method.POST);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("token", token);
                request.AddJsonBody(payload.ToString());

                // 返回值处理
                IRestResponse response = client.Execute(request);
                if (AiResponseUtils.GetLabelAndConfidence(response, ref label, ref confidence) == OneNetResponseUtils.RESPONSE_ERROR)
                {
                    MessageBox.Show("AI识别失败!");
                }
                else
                {
                    MessageBox.Show("图片内容识别为 " + label + "，其置信度为 " + confidence);
                    return true;
                }
            }
            catch (Exception exp)
            {
                MessageBox.Show(exp.Message);
            }

            return false;
        }

        /// <summary>
        /// 向OneNET Studio平台上传文件，该文件会关联到设备上
        /// 官方文档： https://open.iot.10086.cn/doc/iot_platform/book/device-connect&manager/device-file-manager.html
        /// </summary>
        /// <param name="fileFullName">文件全名</param>
        /// <param name="productId">产品ID</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="deviceKey">设备Key</param>
        private string UploadFile(string fileFullName, string productId, string deviceName, string deviceKey)
        {
            // 获取文件名和文件大小
            FileInfo fileInfo = new FileInfo(fileFullName);
            string fileBaseName = fileInfo.Name;
            long fileLength = fileInfo.Length;

            string token = OneNetToken.AssembleSouthToken(productId, deviceName, deviceKey, DateTime.Now.AddDays(90));
            //MessageBox.Show(token);

            try
            {
                string url = string.Format("http://studio-file.heclouds.com/studio/{0}/{1}/outupload", productId, deviceName);
                var client = new RestClient(url);
                client.Timeout = -1;
                var request = new RestRequest(Method.POST);
                request.AddHeader("Content-Type", "multipart/form-data");
                request.AddHeader("Authorization", token);

                request.AddFile("file", fileFullName);
                request.AddParameter("md5", FileUtils.CalFileMD5(fileFullName));
                request.AddParameter("filename", fileBaseName);
                request.AddParameter("size", fileLength);

                // 返回值处理
                IRestResponse response = client.Execute(request);
                string uploadedFileId = OneNetResponseUtils.GetFileUuid(response);
                //MessageBox.Show(uploadedFileId);
                return uploadedFileId;
            }
            catch (Exception exp)
            {
                MessageBox.Show(exp.Message);
            }

            return null;
        }

        /// <summary>
        /// 上传属性至OneNET Studio平台
        /// 官方文档：https://open.iot.10086.cn/doc/v5/develop/detail/690
        /// </summary>
        /// <param name="productId">产品ID</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="deviceKey">设备Key</param>
        /// <param name="payload">上报数据</param>
        /// <returns></returns>
        private bool UploadProperty(string productId, string deviceName, string deviceKey, JObject payload)
        {
            string topic = string.Format("$sys/{0}/{1}/thing/property/post", productId, deviceName);
            string url = string.Format("https://open.iot.10086.cn/studio/http/device/thing/property/post?protocol=http&topic={0}", topic);

            string token = OneNetToken.AssembleSouthToken(productId, deviceName, deviceKey, DateTime.Now.AddDays(90));
            //MessageBox.Show(token);

            try
            {
                var client = new RestClient(url);
                client.Timeout = -1;
                var request = new RestRequest(Method.POST);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("token", token);
                request.AddJsonBody(payload.ToString());

                // 采用同步方式。如果是异步方式，会立即返回，造成json为空
                IRestResponse response = client.Execute(request);
                if (OneNetResponseUtils.GetErrNo(response) == OneNetResponseUtils.RESPONSE_OK)
                {
                    MessageBox.Show("属性上传成功，请前往OneNET Studio查看数据。");
                    return true;
                }
                else
                {
                    MessageBox.Show("属性上传失败！");
                }
            }
            catch (Exception exp)
            {
                MessageBox.Show(exp.Message);
            }

            return false;
        }

        /// <summary>
        /// 构造火灾检测的上传数据
        /// </summary>
        /// <param name="fileFullName">文件全名</param>
        /// <returns></returns>
        private JObject GenFireDetectionPayload(string fileFullName)
        { 
            JArray array = new JArray();
            array.Add(FileUtils.FileToBase64Str(fileFullName));

            JObject payload = new JObject();
            payload["type"] = "GPU";
            payload.Add("picture", array);
            //MessageBox.Show(payload.ToString());

            return payload;
        }

        /// <summary>
        /// 构造烟雾浓度属性，参见物模型
        /// </summary>
        /// <param name="SmokeScope"></param>
        /// <returns></returns>
        private JObject GenSmokeProperty(double SmokeScope)
        { 
            JObject smokeScope = new JObject();
            smokeScope["value"] = Math.Round(SmokeScope,2);

            JObject paramList = new JObject();
            paramList["SmokeScope"] = smokeScope;

            Random rd = new Random();
            JObject payload = new JObject();
            payload["id"] = rd.Next(0, 100).ToString();
            payload["version"] = "1.0";
            payload["params"] = paramList;
            //MessageBox.Show(payload.ToString());

            return payload;
        }

        /// <summary>
        /// 构造AI检测结果属性，参见物模型
        /// </summary>
        /// <param name="imgid">图片ID</param>
        /// <param name="label">类型标签</param>
        /// <param name="confidence">置信度</param>
        /// <returns></returns>
        private JObject GenAiRecordProperty(string imgid, string label, double confidence)
        { 
            JObject values = new JObject();
            values["imgid"] = imgid;
            values["label"] = label;
            values["confidence"] = Math.Round(confidence, 2);

            JObject aiRecord = new JObject();
            aiRecord["value"] = values;

            JObject paramList = new JObject();
            paramList["AiRecord"] = aiRecord;

            Random rd = new Random();
            JObject payload = new JObject();
            payload["id"] = rd.Next(0, 100).ToString();
            payload["version"] = "1.0";
            payload["params"] = paramList;
            //MessageBox.Show(payload.ToString());

            return payload;
        }
    }
}

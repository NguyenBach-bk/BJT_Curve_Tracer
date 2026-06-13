using ScottPlot;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Reflection.Emit;
using System.Windows.Forms;

namespace BJTCurveTracer
{
    public partial class Form1 : Form
    {
        private SerialPort _serialPort;
        private string _rxDataBuffer = "";
        private double _ibStepValue = 20.0;

        private List<double> _batchHfeList = new List<double>();

        private bool _isPowerMode = false;

        private string _hfe1 = "-";
        private string _hfe2 = "-";

        private Dictionary<string, (List<double> X, List<double> Y)> _bjtCurves =
            new Dictionary<string, (List<double> X, List<double> Y)>();

        private Dictionary<string, ScottPlot.Plottables.Scatter> _scatterPlots =
            new Dictionary<string, ScottPlot.Plottables.Scatter>();

        public Form1()
        {
            InitializeComponent();
        }

        // --- 1. SỰ KIỆN KHI FORM VỪA MỞ LÊN ---
        private void Form1_Load(object sender, EventArgs e)
        {
            SetupGraphLayout();
            ScanComPorts(); // Gọi hàm quét cổng COM
        }

        private void SetupGraphLayout()
        {
            formsPlot1.Plot.Clear();
            formsPlot1.Plot.XLabel("Vce (Volts)");
            formsPlot1.Plot.YLabel("Ic (mA)");

            formsPlot1.Plot.Axes.SetLimits(0, 8, 0, 150);
            formsPlot1.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E8E8E8");
            formsPlot1.Refresh();
        }

        // --- HÀM TÌM LẠI BỊ THIẾU: QUÉT CỔNG COM ---
        private void ScanComPorts()
        {
            cmbComPort.Items.Clear();
            string[] availablePorts = SerialPort.GetPortNames();
            cmbComPort.Items.AddRange(availablePorts);
            if (availablePorts.Length > 0)
            {
                cmbComPort.SelectedIndex = 0;
            }
        }

        // --- 2. XỬ LÝ NÚT KẾT NỐI / NGẮT KẾT NỐI ---
        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                if (cmbComPort.SelectedItem == null) return;
                try
                {
                    _serialPort = new SerialPort(cmbComPort.SelectedItem.ToString(), 115200, Parity.None, 8, StopBits.One);
                    _serialPort.DataReceived += SerialPort_OnDataReceived;
                    _serialPort.Open();

                    btnConnect.Text = "Disconnect";
                    lblStatus.Text = "Status: Connected, waiting...";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ERROR: {ex.Message}", "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
                btnConnect.Text = "Connected";
                lblStatus.Text = "Status: Disconnected.";
            }
        }

        // --- 3. SỰ KIỆN CHẠY NGẦM NHẬN DỮ LIỆU TỪ CỔNG COM ---
        private void SerialPort_OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            string incomingChunk = _serialPort.ReadExisting();
            _rxDataBuffer += incomingChunk;

            while (_rxDataBuffer.Contains("\n"))
            {
                int endLineIndex = _rxDataBuffer.IndexOf('\n');
                string packet = _rxDataBuffer.Substring(0, endLineIndex).Trim();
                _rxDataBuffer = _rxDataBuffer.Substring(endLineIndex + 1);

                this.BeginInvoke(new MethodInvoker(() => ParsePacket(packet)));
            }
        }

        // --- 4. PHÂN TÍCH GÓI TIN VÀ VẼ ĐỒ THỊ ---
        private void ParsePacket(string packet)
        {
            // ==========================================================
            // KHỐI 1: XỬ LÝ CHẾ ĐỘ ĐO ĐỒ THỊ ĐẶC TUYẾN ĐƠN LẺ / SO SÁNH
            // ==========================================================
            if (packet.StartsWith("START"))
            {
                formsPlot1.Plot.Clear();
                _bjtCurves.Clear();
                _scatterPlots.Clear();

                formsPlot1.Plot.XLabel("Vce (Volts)");

                _hfe1 = "-";
                _hfe2 = "-";
                formsPlot1.Plot.Title("BJT Characteristic Curves");

                // Mặc định ban đầu đưa về chế độ tín hiệu nhỏ
                _isPowerMode = false;

                if (packet.Contains(":"))
                {
                    try
                    {
                        string[] startTokens = packet.Split(':');
                        string[] cfgTokens = startTokens[1].Split(',');
                        double maxIb = double.Parse(cfgTokens[0], System.Globalization.CultureInfo.InvariantCulture);
                        double numSteps = double.Parse(cfgTokens[1], System.Globalization.CultureInfo.InvariantCulture);
                        _ibStepValue = maxIb / numSteps;

                        // CẢI TIẾN: Nếu dòng kích cực Base lớn nhất >= 1000uA (tức >= 1mA) -> Chuyển sang dải Công suất lớn
                        if (maxIb >= 1000.0)
                        {
                            _isPowerMode = true;
                        }
                    }
                    catch { _ibStepValue = 20.0; }
                }
                else { _ibStepValue = 20.0; }

                // CẢI TIẾN 1: Tự động thay đổi nhãn đơn vị trục Y linh hoạt theo loại BJT
                if (_isPowerMode)
                {
                    formsPlot1.Plot.YLabel("Ic (A)");
                }
                else
                {
                    formsPlot1.Plot.YLabel("Ic (mA)");
                }

                lblStatus.Text = "Status: Receiving sweep data...";
                formsPlot1.Refresh();
            }
            else if (packet.StartsWith("DATA:"))
            {
                string[] bodyTokens = packet.Substring(5).Split(',');
                if (bodyTokens.Length == 4)
                {
                    int bjtIdx = int.Parse(bodyTokens[0]);
                    int stepIdx = int.Parse(bodyTokens[1]);
                    double vceVal = double.Parse(bodyTokens[2], System.Globalization.CultureInfo.InvariantCulture);
                    double icVal = double.Parse(bodyTokens[3], System.Globalization.CultureInfo.InvariantCulture);

                    // CẢI TIẾN 1: Nếu đo BJT công suất, ép chia dòng Ic cho 1000.0 để đổi từ mA sang Ampe (A) đúng tỷ lệ đồ thị
                    if (_isPowerMode)
                    {
                        icVal = icVal / 1000.0;
                    }

                    string uniqueCurveId = $"BJT{bjtIdx}_Step{stepIdx}";

                    if (!_bjtCurves.ContainsKey(uniqueCurveId))
                    {
                        _bjtCurves[uniqueCurveId] = (new List<double>(), new List<double>());
                    }

                    _bjtCurves[uniqueCurveId].X.Add(vceVal);
                    _bjtCurves[uniqueCurveId].Y.Add(icVal);

                    if (_scatterPlots.ContainsKey(uniqueCurveId))
                    {
                        formsPlot1.Plot.Remove(_scatterPlots[uniqueCurveId]);
                    }

                    var scatterLine = formsPlot1.Plot.Add.Scatter(_bjtCurves[uniqueCurveId].X.ToArray(), _bjtCurves[uniqueCurveId].Y.ToArray());
                    scatterLine.LineWidth = 2;
                    scatterLine.MarkerStyle.Size = 0;
                    scatterLine.Color = (bjtIdx == 1) ? ScottPlot.Colors.DodgerBlue : ScottPlot.Colors.Orange;

                    _scatterPlots[uniqueCurveId] = scatterLine;

                    if (bjtIdx == 1 && _bjtCurves[uniqueCurveId].X.Count == 100)
                    {
                        double currentIbValue = (stepIdx + 1) * _ibStepValue;

                        string ibLabel;
                        if (currentIbValue >= 1000)
                        {
                            ibLabel = $"Ib={(currentIbValue / 1000.0):F1}mA";
                        }
                        else
                        {
                            ibLabel = $"Ib={currentIbValue:F0}uA";
                        }

                        // SỬA LỖI 1: Thay thế chuỗi khóa cứng uA cũ bằng biến "ibLabel" thông minh để hiện mA khi cần
                        var txt = formsPlot1.Plot.Add.Text(ibLabel, vceVal, icVal);
                        txt.LabelFontSize = 10;
                        txt.LabelFontColor = ScottPlot.Colors.Black;
                        txt.LabelAlignment = Alignment.LowerRight;
                    }

                    formsPlot1.Plot.Axes.AutoScale();
                    formsPlot1.Plot.Axes.SetLimitsX(0, 8);
                    formsPlot1.Refresh();
                }
            }
            else if (packet.StartsWith("DONE:"))
            {
                // ĐÃ SỬA: Gom toàn bộ logic DONE quét đơn về một vị trí duy nhất để chống chặn dòng logic
                string[] endTokens = packet.Substring(5).Split(',');
                if (endTokens.Length == 2)
                {
                    string bjtIdx = endTokens[0];
                    string hfeVal = endTokens[1];

                    if (bjtIdx == "1")
                    {
                        // Lưu lại hFE của con thứ nhất vào biến toàn cục
                        _hfe1 = hfeVal;
                        lblStatus.Text = $"Status: BJT 1 sweep completed! (hFE = {_hfe1})";

                        // Đẩy thông số trực tiếp lên tiêu đề đồ thị
                        formsPlot1.Plot.Title($"BJT 1 hFE = {_hfe1}");
                    }
                    else if (bjtIdx == "2")
                    {
                        // Lưu lại hFE của con thứ hai vào biến toàn cục
                        _hfe2 = hfeVal;

                        // Hiển thị đồng thời cả 2 kết quả trên thanh trạng thái, giữ nguyên giá trị BJT 1 không cho ghi đè
                        lblStatus.Text = $"Status: Compare Mode -> BJT 1 hFE = {_hfe1} | BJT 2 hFE = {_hfe2}";

                        // Cập nhật tiêu đề đồ thị dạng đối chiếu cặp đôi
                        formsPlot1.Plot.Title($"BJT 1 hFE = {_hfe1} vs BJT 2 hFE = {_hfe2}");
                    }

                    formsPlot1.Refresh(); // Cập nhật Title lên giao diện đồ thị
                }
            }

            // ==========================================================
            // KHỐI 2: XỬ LÝ CHẾ ĐỘ KIỂM TRA LÔ LINH KIỆN ĐỒNG BỘ 3-SIGMA
            // ==========================================================
            else if (packet.StartsWith("BATCH_START:"))
            {
                formsPlot1.Plot.Clear();
                formsPlot1.Plot.XLabel("BJT Order Index (ID)");
                formsPlot1.Plot.YLabel("Measured Gain (hFE)");

                _batchHfeList.Clear();

                string sizeStr = packet.Substring(12);
                lblStatus.Text = $"Status: Batch test started. Size: {sizeStr} pcs. Measuring BJT #1...";
                formsPlot1.Refresh();
            }
            else if (packet.StartsWith("BATCH_DATA:"))
            {
                string[] batchTokens = packet.Substring(11).Split(',');
                if (batchTokens.Length == 2)
                {
                    double id = double.Parse(batchTokens[0]);
                    double hfe = double.Parse(batchTokens[1], System.Globalization.CultureInfo.InvariantCulture);

                    _batchHfeList.Add(hfe);

                    var dot = formsPlot1.Plot.Add.Marker(id, hfe);
                    dot.MarkerStyle.Shape = MarkerShape.FilledCircle;
                    dot.MarkerStyle.Size = 8;
                    dot.Color = ScottPlot.Colors.Orange;

                    lblStatus.Text = $"Status: Measured BJT #{id} -> hFE = {hfe}";

                    formsPlot1.Plot.Axes.AutoScale();
                    formsPlot1.Refresh();
                }
            }
            else if (packet.StartsWith("BATCH_DONE:"))
            {
                // KHÔI PHỤC: Trả lại tính năng xử lý kết quả đo lô 3-Sigma và xuất file Excel vốn bị trùng lặp đè mất trước đó
                string[] reportTokens = packet.Substring(11).Split(',');
                if (reportTokens.Length == 4)
                {
                    double mean = double.Parse(reportTokens[0], System.Globalization.CultureInfo.InvariantCulture);
                    double stdDev = double.Parse(reportTokens[1], System.Globalization.CultureInfo.InvariantCulture);
                    double cv = double.Parse(reportTokens[2], System.Globalization.CultureInfo.InvariantCulture);
                    int outliers = int.Parse(reportTokens[3]);

                    // Vẽ đường thẳng Mean
                    var meanLine = formsPlot1.Plot.Add.HorizontalLine(mean);
                    meanLine.Color = ScottPlot.Colors.Green;
                    meanLine.LinePattern = LinePattern.Dashed;

                    // Vẽ đường thẳng giới hạn chất lượng +3 Sigma
                    var upperLine = formsPlot1.Plot.Add.HorizontalLine(mean + (3.0 * stdDev));
                    upperLine.Color = ScottPlot.Colors.Red;
                    upperLine.LinePattern = LinePattern.Dashed;

                    // Vẽ đường thẳng giới hạn chất lượng -3 Sigma
                    var lowerLine = formsPlot1.Plot.Add.HorizontalLine(mean - (3.0 * stdDev));
                    lowerLine.Color = ScottPlot.Colors.Red;
                    lowerLine.LinePattern = LinePattern.Dashed;

                    // Chèn chữ nhãn thích ứng
                    var txtMean = formsPlot1.Plot.Add.Text($"Mean={mean:F1}", 0.5, mean);
                    txtMean.LabelFontColor = ScottPlot.Colors.Green;
                    txtMean.LabelFontSize = 9;
                    txtMean.LabelAlignment = Alignment.LowerLeft;

                    var txtUpper = formsPlot1.Plot.Add.Text($"+3σ={(mean + 3.0 * stdDev):F1}", 0.5, mean + 3.0 * stdDev);
                    txtUpper.LabelFontColor = ScottPlot.Colors.Red;
                    txtUpper.LabelFontSize = 9;
                    txtUpper.LabelAlignment = Alignment.LowerLeft;

                    var txtLower = formsPlot1.Plot.Add.Text($"-3σ={(mean - 3.0 * stdDev):F1}", 0.5, mean - 3.0 * stdDev);
                    txtLower.LabelFontColor = ScottPlot.Colors.Red;
                    txtLower.LabelFontSize = 9;
                    txtLower.LabelAlignment = Alignment.UpperLeft;

                    formsPlot1.Plot.Axes.AutoScale();
                    formsPlot1.Refresh();

                    MessageBox.Show(
                        $"--- BATCH TEST 3-SIGMA REPORT ---\n\n" +
                        $"Total Samples:\t{_batchHfeList.Count} pcs\n" +
                        $"Mean hFE (u):\t{mean:F1}\n" +
                        $"Std Dev (o):\t{stdDev:F2}\n" +
                        $"Variation (CV):\t{cv:F1}%\n" +
                        $"Outliers (Bad):\t{outliers} pcs\n\n" +
                        $"Batch Assessment: {(cv <= 5.0 ? "PASS - High Uniformity" : "FAIL - High Dispersion")}",
                        "3-Sigma Statistical Analysis Results",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );

                    DialogResult exportChoice = MessageBox.Show(
                        "Do you want to export the batch quality statistics to an Excel file?",
                        "Export Report",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question
                    );

                    if (exportChoice == DialogResult.Yes)
                    {
                        ExportBatchToExcelCsv(mean, stdDev, cv, outliers);
                    }

                    lblStatus.Text = $"Status: Batch analysis completed. Mean hFE = {mean:F1}.";
                }
            }
        }

        private void ExportBatchToExcelCsv(double mean, double stdDev, double cv, int outliers)
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "Excel CSV Files (*.csv)|*.csv";
                sfd.FileName = $"BJT_3Sigma_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                sfd.Title = "Select location to save batch quality report";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (System.IO.StreamWriter sw = new System.IO.StreamWriter(sfd.FileName, false, new System.Text.UTF8Encoding(true)))
                        {
                            sw.WriteLine("sep=,");

                            // 1. Xuất file thông số tổng hợp bằng tiếng Anh
                            sw.WriteLine("--- BJT BATCH QUALITY STATISTICAL REPORT (3-SIGMA) ---");
                            sw.WriteLine();

                            sw.WriteLine($"Execution Time:,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            sw.WriteLine($"Total Samples:,{_batchHfeList.Count} pcs");
                            sw.WriteLine($"Mean hFE (u):,{mean.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                            sw.WriteLine($"Standard Deviation (o):,{stdDev.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
                            sw.WriteLine($"Coefficient of Variation (CV):,{cv.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%");
                            sw.WriteLine($"Outliers (Bad Count):,{outliers} pcs");
                            sw.WriteLine($"Batch Quality Assessment:,{(cv <= 5.0 ? "PASS - High Batch Uniformity" : "FAIL - High Dispersion")}");
                            sw.WriteLine();

                            // 2. Tiêu đề bảng danh sách chi tiết (Đồng bộ nhãn với linh kiện nhúng)
                            sw.WriteLine("Component ID,Measured Value (hFE),Quality Status (3-Sigma)");

                            double upperLimit = mean + 3.0 * stdDev;
                            double lowerLimit = mean - 3.0 * stdDev;

                            for (int i = 0; i < _batchHfeList.Count; i++)
                            {
                                double hfe = _batchHfeList[i];
                                // Sử dụng đúng cụm FAILED (OUTLIER) và PASS của STM32
                                string status = (hfe > upperLimit || hfe < lowerLimit) ? "FAILED (OUTLIER)" : "PASS";

                                sw.WriteLine($"{i + 1},{hfe.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},{status}");
                            }
                        }
                        MessageBox.Show("Batch report exported and formatted successfully!", "Notification", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"System error writing file: {ex.Message}", "Data Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // --- 5. BẢO VỆ PHẦN CỨNG KHI TẮT APP ---
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            base.OnFormClosing(e);
        }

        private void cmbComPort_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void refresh_COM_Click(object sender, EventArgs e)
        {
            ScanComPorts();
        }
    }
}
/* Copyright 2016 by Northwestern University
 * Authors: Y. Curtis Wang and Terence C. Chan
 * 
 * Namespace: NsfSwitchControl
 * Description: This namespace implements controllers for collecting impedance and temperature data
 * using a National Instruments PXIe-2529 matrix switch, National Instruments PXIe-4357 RTD DAQ,
 * and a Hameg HM8118 LCR meter. The matrix switch controller switches between applying power
 * and connecting the LCR meter based on the desired duty cycle. The temperature controller
 * does not currently sync with the impedance controller but does sync with system time.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Windows;

using NationalInstruments;
using NationalInstruments.ModularInstruments.NISwitch;
using NationalInstruments.ModularInstruments.SystemServices.DeviceServices;
using NationalInstruments.Visa;
using NationalInstruments.DAQmx;

namespace NsfSwitchControl
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private TemperatureMeasurementController tempMeasCont;
        private LcrMeterController lcrMeterCont;
        private System.Windows.Threading.DispatcherTimer elapsedTimer;
        private const int fileRefreshIntervalSeconds = 5;
        private DateTime startDateTime;
        private ImpedanceMeasurementController impMeasCont;
        private delegate void TextBoxUpdateDelegate(DataTable dtForGrid, System.Windows.Controls.DataGrid theDataGrid);

        private string __dtImpName;
        private string __dtTempName;

        public MainWindow()
        {
            InitializeComponent();
            CheckHardwareStatus();
        }


        private void CheckHardwareStatus()
        {
            // check for switches, which should be the matrix switch
            ModularInstrumentsSystem modularInstrumentsSystem = new ModularInstrumentsSystem("NI-SWITCH");
            if (modularInstrumentsSystem.DeviceCollection.Count == 1 && modularInstrumentsSystem.DeviceCollection[0].Model == "NI PXIe-2529")
            {
                labelSwitchConnectionStatus.Content = "PXIe-2529 128-Connection OK";
                labelSwitchConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                buttonInitializeControllers.IsEnabled = true;
            }
            else
            {
                labelSwitchConnectionStatus.Content = "Problem with PXIe-2529 128-Connection";
                labelSwitchConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                buttonInitializeControllers.IsEnabled = false;
            }

            // check for DAQs, which should be temperature system
            var daq_channels = DaqSystem.Local.GetPhysicalChannels(PhysicalChannelTypes.AI, PhysicalChannelAccess.External);
            if (daq_channels.Count() >= 20)
            {
                labelTemperatureConnectionStatus.Content = "PXIe-4357 20-Channel OK";
                labelTemperatureConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                buttonInitializeControllers.IsEnabled = true;
            }
            else
            {
                labelTemperatureConnectionStatus.Content = "Problem with PXIe-4357 20-Channel";
                labelTemperatureConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                buttonInitializeControllers.IsEnabled = false;
            }

            // check for HM8118 via NI-VISA *IDN?\r command
            lcrMeterCont = new LcrMeterController();
            if (lcrMeterCont.IsEnabled == true && lcrMeterCont.TestConnection() == true)
            {
                labelImpedanceConnectionStatus.Content = "HM8118 VISA OK";
                labelImpedanceConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                buttonInitializeControllers.IsEnabled = true;
            }
            else
            {
                labelImpedanceConnectionStatus.Content = "Problem with HM8118 VISA";
                labelImpedanceConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                buttonInitializeControllers.IsEnabled = false;
            }
            lcrMeterCont = null;
        }


        private void InitializeSwitchAndTemperatureControllers(string saveFileLocationFolder)
        {
            string saveFileLocation = saveFileLocationFolder + "/" + DateTime.Now.ToString("yyyy.MM.dd") + "-" + DateTime.Now.ToString("HH.mm");
            int impedanceMeasurementInterval = int.Parse(textboxImpMeasIntervalDesired.Text);
            int totalNumberOfImpMeasSamples = int.Parse(textboxImpMeasSamplesDesired.Text);
            impMeasCont = new ImpedanceMeasurementController(impedanceMeasurementInterval, totalNumberOfImpMeasSamples, saveFileLocation, this);
            tempMeasCont = new TemperatureMeasurementController(saveFileLocation, this);
        }


        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var fileDialog = new System.Windows.Forms.FolderBrowserDialog();
            var result = fileDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
                textboxFolderPath.Text = fileDialog.SelectedPath;
        }


        private void buttonInitializeControllers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (textboxFolderPath.Text != "" && int.Parse(textboxImpMeasSamplesDesired.Text) >= 2)
                {
                    InitializeSwitchAndTemperatureControllers(textboxFolderPath.Text);
                    buttonInitializeControllers.IsEnabled = false;
                    buttonStartCollection.IsEnabled = true;
                    labelControllerStatus.Content = "Initialized";
                    labelControllerStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
                }
                else
                    System.Windows.MessageBox.Show("You forgot to choose a path or the number of samples is less than 2!");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString());
            }
        }


        private void buttonStartCollection_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont.StartMeasurement();
            impMeasCont.StartCollection();
            startDateTime = DateTime.Now;
            elapsedTimer = new System.Windows.Threading.DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), System.Windows.Threading.DispatcherPriority.Normal, delegate
            {
                labelTimeElapsed.Content = (DateTime.Now.Subtract(startDateTime)).ToString(@"mm\:ss");
                if (impMeasCont.IsComplete)
                    StopCollection(true);
            }, this.Dispatcher);
            buttonStartCollection.IsEnabled = false;
            buttonStopCollection.IsEnabled = true;
            labelControllerStatus.Content = "Running...";
        }


        private void StopCollection(bool isComplete)
        {
            tempMeasCont.StopMeasurement();
            impMeasCont.StopCollection();
            elapsedTimer.IsEnabled = false;
            buttonStopCollection.IsEnabled = false;
            buttonFlushSystem.IsEnabled = true;
            if (isComplete)
            {
                labelControllerStatus.Content = "Complete";
                labelControllerStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            else
                labelControllerStatus.Content = "Stopped";
        }


        private void buttonStopCollection_Click(object sender, RoutedEventArgs e)
        {
            StopCollection(false);
        }


        private void buttonFlushSystem_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont = null;
            buttonInitializeControllers.IsEnabled = true;
            buttonFlushSystem.IsEnabled = false;
            labelControllerStatus.Content = "Flushed";
        }

        public void addLineToImpedanceBox(DataTable dtForGrid)
        {
            if (__dtImpName != dtForGrid.TableName)
            {
                __dtImpName = dtForGrid.TableName;
                datagridImpedance.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(rebindDatagrid), dtForGrid, datagridImpedance);
            }
            else
            {
                datagridImpedance.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(scrollDatagrid), dtForGrid, datagridImpedance);
            }
        }

        public void addLineToTemperatureBox(DataTable dtForGrid)
        {
            if (__dtTempName != dtForGrid.TableName)
            {
                __dtTempName = dtForGrid.TableName;
                datagridTemperature.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(rebindDatagrid), dtForGrid, datagridTemperature);
            }
            else
            {
                datagridTemperature.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(scrollDatagrid), dtForGrid, datagridTemperature);
            }
        }

        private void rebindDatagrid(DataTable dtForGrid, System.Windows.Controls.DataGrid theDataGrid)
        {
            theDataGrid.ItemsSource = dtForGrid.DefaultView;
            theDataGrid.AutoGenerateColumns = true;
        }

        private void scrollDatagrid(DataTable dtForGrid, System.Windows.Controls.DataGrid theDataGrid)
        {
            int count = theDataGrid.Items.Count;
            if (theDataGrid.Items.Count > 0)
                theDataGrid.ScrollIntoView(theDataGrid.Items.GetItemAt(theDataGrid.Items.Count - 1));
        }
    }


    public class SwitchMatrixController
    {
        private NISwitch switchSession;

        private const string __switchAddress = "PXI1Slot6";
        private const string __switchTopology = "2529/2-Wire 4x32 Matrix";
        private const string __lcrMeterPositive = "r0";
        private const string __lcrMeterNegative = "r1";
        private const string __rfGeneratorPositive = "r2";
        private const string __rfGeneratorNegative = "r3";
        private const string __rfGeneratorSwitch = "c31";
        private readonly PrecisionTimeSpan maxTime = new PrecisionTimeSpan(50.0);

        public SwitchMatrixController()
        {
            try
            {
                switchSession = new NISwitch(__switchAddress, __switchTopology, false, true);
                switchSession.DriverOperation.Warning += new System.EventHandler<SwitchWarningEventArgs>(DriverOperationWarning);
                switchSession.Path.DisconnectAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SwitchMatrixController() failure");
                throw new Exception("Error: Could not create SwitchMatrixController!");
            }
        }

        ~SwitchMatrixController()
        {
            DisconnectAll();
        }


        public void DisconnectAll()
        {
            try
            {
                switchSession.Path.DisconnectAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DisconnectAll() Error!");
            }
        }


        public bool Connect(Dictionary<string, List<string>> switchList, bool ablation)
        {
            try
            {
                switchSession.Path.DisconnectAll();
                string posTerminal, negTerminal;

                if (ablation)
                {
                    posTerminal = __rfGeneratorPositive;
                    negTerminal = __rfGeneratorNegative;
                }
                else
                {
                    posTerminal = __lcrMeterPositive;
                    negTerminal = __lcrMeterNegative;
                }
                string connectionList = "";
                foreach (string channel in switchList["Positive"])
                {
                    connectionList += posTerminal + "->" + channel + ",";
                }
                connectionList = connectionList.Remove(connectionList.Length - 1);
                switchSession.Path.ConnectMultiple(connectionList);
                connectionList = "";
                foreach (string channel in switchList["Negative"])
                {
                    connectionList += negTerminal + "->" + channel + ",";
                }
                connectionList = connectionList.Remove(connectionList.Length - 1);
                switchSession.Path.ConnectMultiple(connectionList);
                switchSession.Path.WaitForDebounce(maxTime);

                if (ablation)
                {
                    // connect c31 to __rfGeneratorNegative to turn it all on
                    switchSession.Path.Connect(__rfGeneratorNegative, __rfGeneratorSwitch);
                    switchSession.Path.WaitForDebounce(maxTime);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SwitchMatrixController.UseNextAblationPermutation() failure");
                return false;
            }
        }


        private void DriverOperationWarning(object sender, SwitchWarningEventArgs e)
        {
            MessageBox.Show(e.ToString(), "Warning");
        }


        private void CloseSession()
        {
            if (switchSession != null)
            {
                try
                {
                    switchSession.Close();
                    switchSession = null;
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show("Unable to Close Session, Reset the device.\n" + "Error : " + ex.Message, "SwitchMatrixController.CloseSession() failure");
                }
            }
        }
    }

    
    public class ImpedanceMeasurementController
    {
        private System.IO.StreamWriter dataWriteFile;
        private List<Dictionary<string, List<string>>> impedanceSwitchGroups;
        private List<Dictionary<string, List<string>>> ablationSwitchGroups;
        private SwitchMatrixController swMatCont;
        private LcrMeterController lcrMeterCont;

        // multithreading handles
        private System.Threading.Timer collectionTimer;
        private System.Threading.TimerCallback collectCallback;
        private bool collectData = false;
        public bool IsComplete = false;
        private bool preAblation = true;

        private int __totalNumberOfSamples;
        private int __currentNumberOfSamples;
        private string __dataTableHeader;
        private const int __preAblationMilliseconds = 5000; // TODO: change this if needed
        private int __measurementInterval;
        static private readonly List<string> __groupN = new List<string> { "c0", "c1", "c2", "c3" };
        static private readonly List<string> __groupE = new List<string> { "c4", "c5", "c6", "c7" };
        static private readonly List<string> __groupS = new List<string> { "c8", "c9", "c10", "c11" };
        static private readonly List<string> __groupW = new List<string> { "c12", "c13", "c14", "c15" };
        static private readonly List<string> __groupB = new List<string> { "c16", "c17", "c18", "c19" };
        static private readonly List<string> __groupT = new List<string> { "c20" };
        static private readonly List<string> __groupX = new List<string> { "c25" };
        static private readonly List<string> __groupY = new List<string> { "c26" };
        static private readonly List<string> __groupZ = new List<string> { "c27" };
        static private readonly List<string> __groupAllInternal = __groupN.Concat(__groupE).Concat(__groupS).Concat(__groupW).Concat(__groupB).Concat(__groupT).ToList();
        static private readonly List<string> __externalElectrodes = new List<string> { "X", "Y", "Z" };
        static private readonly List<string> __internalElectrodes = new List<string> { "AllInternal", "N", "E", "S", "W", "B", "T" };

        private MainWindow mainRef;
        private DataTable __datatableImpedance = new DataTable("impedance");

        public ImpedanceMeasurementController(int measurementInterval, int sampleTotal, string saveFileLocation, MainWindow mainRefIn)
        {
            __totalNumberOfSamples = sampleTotal;
            __currentNumberOfSamples = 0;
            __measurementInterval = measurementInterval*1000;
            lcrMeterCont = new LcrMeterController();

            impedanceSwitchGroups = new List<Dictionary<string, List<string>>>();
            // external-to-internal impedance measurement permutations
            foreach (string extCode in __externalElectrodes)
            {
                foreach (string intCode in __internalElectrodes)
                {
                    impedanceSwitchGroups.Add(ConvertPositiveNegativeFaceCodeToPermutation(intCode, extCode));
                }
            }
            // external-to-external impedance measurement permutations
            List<string> alreadyUsed = new List<string>();
            foreach (string extCode1 in __externalElectrodes)
            {
                foreach (string extCode2 in __externalElectrodes)
                {
                    if ((extCode1 != extCode2) && (alreadyUsed.Contains(extCode2) == false))
                    {
                        impedanceSwitchGroups.Add(ConvertPositiveNegativeFaceCodeToPermutation(extCode1, extCode2));
                    }
                }
                alreadyUsed.Add(extCode1);
            }
            // internal-to-internal impedance measurement permutations
            //List<string> alreadyUsed = new List<string>();
            alreadyUsed.Clear();
            foreach (string intCode1 in __internalElectrodes.Skip(1))
            {
                foreach (string intCode2 in __internalElectrodes.Skip(1))
                {
                    if ((intCode1 != intCode2) && (alreadyUsed.Contains(intCode2) == false))
                    {
                        impedanceSwitchGroups.Add(ConvertPositiveNegativeFaceCodeToPermutation(intCode1, intCode2));
                    }
                }
                alreadyUsed.Add(intCode1);
            }

            ablationSwitchGroups = new List<Dictionary<string, List<string>>> { new Dictionary<string, List<string>>{
                { "Positive", new List<string> { "c0", "c1", "c4", "c5", "c8", "c9", "c12", "c13", "c16", "c17", "c20" } },
                { "Negative", new List<string> { "c2", "c3", "c6", "c7", "c10", "c11", "c14", "c15", "c18", "c19" } }
            } };

            swMatCont = new SwitchMatrixController();
            collectCallback = new System.Threading.TimerCallback(CollectData);

            dataWriteFile = new System.IO.StreamWriter(saveFileLocation + ".impedance.csv");
            __dataTableHeader = "date,time,pos,neg,impedance (ohms),phase (deg)";

            foreach (string col in __dataTableHeader.Split(','))
            {
                __datatableImpedance.Columns.Add(col);
            }
            // I'm thinking, maybe just have it be "date, time, pos, neg, impedance, phase"
            // and let weka/tensorflow deal with sorting out the time and permutation
            dataWriteFile.WriteLine(__dataTableHeader);

            mainRef = mainRefIn;
            mainRef.addLineToImpedanceBox(__datatableImpedance);
            //mainRef.addLineToImpedanceBox(__dataTableHeader);
        }


        // pass in "N", then return __groupN
        private List<string> ConvertFaceCodeToColumns(string in_code)
        {
            switch (in_code)
            {
                case "N":
                    return __groupN;
                case "E":
                    return __groupE;
                case "S":
                    return __groupS;
                case "W":
                    return __groupW;
                case "B":
                    return __groupB;
                case "T":
                    return __groupT;
                case "X":
                    return __groupX;
                case "Y":
                    return __groupY;
                case "Z":
                    return __groupZ;
                case "AllInternal":
                    return __groupAllInternal;
                default:
                    return new List<string>();
            }
        }


        // generate permutation groups for Positive and Negative based on NESWTBXYZ, like pass in (["N",
        // TODO: possibly look at multiple faces to one face or something in the future
        private Dictionary<string, List<string>> ConvertPositiveNegativeFaceCodeToPermutation(string posCode, string negCode)
        {
            List<string> posColumns = ConvertFaceCodeToColumns(posCode);
            List<string> negColumns = ConvertFaceCodeToColumns(negCode);
            Dictionary<string, List<string>> returnDict = new Dictionary<string, List<string>> { {"PositiveCode", new List<string>{ posCode } },
                { "Positive", posColumns}, { "NegativeCode", new List<string> { negCode } }, { "Negative", negColumns } };
            return returnDict;
        }


        public bool StartCollection()
        {
            collectData = true;
            collectionTimer = new System.Threading.Timer(collectCallback, this, 0, System.Threading.Timeout.Infinite);
            return false;
        }


        public bool StopCollection()
        {
            collectData = false;
            swMatCont.DisconnectAll();
            return false;
        }


        private void CollectData(object stateInf)
        {
            collectionTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            if (collectData && (__currentNumberOfSamples < __totalNumberOfSamples))
            {
                foreach (Dictionary<string, List<string>> permutation in impedanceSwitchGroups)
                {
                    if (collectData)
                    {
                        swMatCont.Connect(permutation, false);
                        while (!lcrMeterCont.IsLCRMeterReady()){ };
                        string impPhaseDataRaw = lcrMeterCont.GetZThetaValue();
                        Tuple<string, string> impPhaseDataSeparated = ConvertLcrXallToImpedanceAndPhase(impPhaseDataRaw);
                        WritePermutationImpedanceToFile(permutation, impPhaseDataSeparated.Item1, impPhaseDataSeparated.Item2);
                    }
                    else
                    {
                        dataWriteFile.Close();
                        collectionTimer.Dispose();
                        IsComplete = true;
                        return;
                    }
                }
                swMatCont.DisconnectAll();

                if (preAblation)
                {
                    swMatCont.Connect(ablationSwitchGroups[0], true);
                    preAblation = false;
                    collectionTimer.Change(__preAblationMilliseconds, System.Threading.Timeout.Infinite);
                }
                else if (__currentNumberOfSamples < (__totalNumberOfSamples - 1))
                {
                    __currentNumberOfSamples++;
                    swMatCont.Connect(ablationSwitchGroups[0], true);
                    collectionTimer.Change(__measurementInterval, System.Threading.Timeout.Infinite);
                }
                else
                {
                    dataWriteFile.Close();
                    collectionTimer.Dispose();
                    IsComplete = true;
                    return;
                }
                return;
            }
            else
            {
                dataWriteFile.Close();
                collectionTimer.Dispose();
                IsComplete = true;
                return;
            }
        }


        private Tuple<string, string> ConvertLcrXallToImpedanceAndPhase(string lcrXallIn)
        {
            string[] splitString = lcrXallIn.Split(',');
            return new Tuple<string, string>(splitString[0], splitString[1]);
        }


        private void WritePermutationImpedanceToFile(Dictionary<string, List<string>> permutation, string impedance, string phase)
        {
            string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
            string currentTime = DateTime.Now.ToString("hh:mm:ss.fff");
            string posCode = permutation["PositiveCode"][0];
            string negCode = permutation["NegativeCode"][0];
            string lineToWrite = currentDate + "," + currentTime + "," + posCode + "," + negCode + "," + impedance + "," + phase;
            dataWriteFile.WriteLine(lineToWrite);

            DataRow dataRow = __datatableImpedance.NewRow();
            dataRow[0] = currentDate;
            dataRow[1] = currentTime;
            dataRow[2] = posCode;
            dataRow[3] = negCode;
            dataRow[4] = impedance;
            dataRow[5] = phase;
            __datatableImpedance.Rows.Add(dataRow);
            mainRef.addLineToImpedanceBox(__datatableImpedance);
        }


        private string ListStringToString(List<string> listString)
        {
            string output = "";
            foreach (string str in listString)
            {
                output += str + ",";
            }
            return output;
        }

    }


    public class LcrMeterController
    {
        private NationalInstruments.Visa.ResourceManager rmSession;
        private NationalInstruments.Visa.MessageBasedSession mbSession;
        public bool IsEnabled;

        private const string __lcrMeterPort = "ASRL3::INSTR";
        private const int __lcrFrequency = 100000;
        private const string __termchar = "\r";

        public LcrMeterController()
        {
            try
            {
                rmSession = new ResourceManager();
                mbSession = (MessageBasedSession)rmSession.Open(__lcrMeterPort);
                mbSession.SynchronizeCallbacks = true;
                mbSession.TerminationCharacter = (byte)'\r';
                mbSession.TerminationCharacterEnabled = true;
                mbSession.SendEndEnabled = false;
                mbSession.RawIO.Write("*RCL 0" + __termchar);
                //System.Threading.Thread.Sleep(1000);
                bool LcrReady = IsLCRMeterReady();
                if (LcrReady == false)
                    throw new Exception("The LCR meter timed out...");
                this.SetLcrMeterFrequency(__lcrFrequency);
                IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to connect to the HM8118, check the port. \nError: " + ex.Message, "ImpedanceMeasurementController constructor failure");
                IsEnabled = false;
            }
        }


        public bool TestConnection() {
            try
            {
                mbSession.RawIO.Write("*IDN?" + __termchar);
                string response = mbSession.RawIO.ReadString();
                if (response == "HAMEG Instruments, HM8118,026608583,1.57\r")
                    return true;
                else
                    return false;
            }
            catch (Ivi.Visa.IOTimeoutException ex)
            {
                MessageBox.Show(ex.Message, "TestConnection() Failure");
                return false;
            }
        }


        // busy wait to check if LCR meter
        public bool IsLCRMeterReady()
        {
            for (int i = 0; i < 100; ++i) {
                try
                {
                    mbSession.RawIO.Write("*OPC?" + __termchar);
                    string response = mbSession.RawIO.ReadString();
                    if (response == "1\r")
                        return true;
                }
                catch
                {
                    continue;
                }
            }
            return false;
        }


        // remember to check if LCR meter is ready first
        public string GetZThetaValue()
        {
            try
            {
                mbSession.RawIO.Write("XALL?" + __termchar);
                return (mbSession.RawIO.ReadString());
            }
            catch (Ivi.Visa.IOTimeoutException ex)
            {
                MessageBox.Show(ex.Message, "GetZThetaValue() Failure");
                return "";
            }
            catch (System.NullReferenceException ex)
            {
                MessageBox.Show(ex.Message, "LCR Meter connection failure!");
                return "";
            }
        }


        public bool SetLcrMeterFrequency(int desiredFreq)
        {
            try
            {
                mbSession.RawIO.Write("FREQ "+ desiredFreq.ToString() + __termchar);
                mbSession.RawIO.Write("FREQ?" + __termchar);
                if (mbSession.RawIO.ReadString() == desiredFreq.ToString()+"\r")
                    return true;
                else
                    return false;
            }
            catch (Ivi.Visa.IOTimeoutException ex)
            {
                MessageBox.Show(ex.Message, "SetLcrMeterFrequency() Failure");
                return false;
            }
        }

    }


    public class TemperatureMeasurementController
    {
        private AnalogMultiChannelReader analogInReader;
        private AsyncCallback myAsyncCallback;
        private NationalInstruments.DAQmx.Task myTask;
        private NationalInstruments.DAQmx.Task runningTask;
        private List<string> channelsToUseAddresses = new List<string>();
        private System.IO.StreamWriter dataWriteFile;

        // RTD physical configuration
        private readonly List<int> __channelsToUse = new List<int>(Enumerable.Range(0, 20)); // use all of the channels
        private const string __pxiLocation = "PXI1Slot2";
        private readonly AIRtdType __rtdType = AIRtdType.Pt3851;
        private const double __r0Numeric = 100.0;
        private readonly AIResistanceConfiguration __resistanceConfiguration = AIResistanceConfiguration.ThreeWire;
        private readonly AIExcitationSource __excitationSource = AIExcitationSource.Internal;
        private readonly double __minimumValueNumeric = 0.0;
        private readonly double __maximumValueNumeric = 200.0;
        private readonly AITemperatureUnits __temperatureUnit = AITemperatureUnits.DegreesC;
        private readonly double __currentExcitationNumeric = 900e-6;

        // Sampling configuration
        private const double __sampleRate = 2.0; // in Hz
        private const int __samplesPerChannelBeforeRelease = 1;

        // Data table configuration
        private string __dataTableHeader;

        private MainWindow mainRef;
        private DataTable __datatableTemperature = new DataTable("temperature");


        public TemperatureMeasurementController(string saveFileLocation, MainWindow mainRefIn)
        {
            // the USB daq uses "cDAQ1Mod1/aiX" as its location
            __datatableTemperature.Columns.Add("Date");
            __datatableTemperature.Columns.Add("Time");
            foreach (int channelId in __channelsToUse)
            {
                channelsToUseAddresses.Add(__pxiLocation + "/ai" + channelId.ToString());
                __datatableTemperature.Columns.Add(channelId.ToString());
            }
            dataWriteFile = new System.IO.StreamWriter(saveFileLocation+".temperature.csv");
            __dataTableHeader = "date,time," + String.Join(",", channelsToUseAddresses);
            dataWriteFile.WriteLine(__dataTableHeader);
            mainRef = mainRefIn;
            mainRef.addLineToTemperatureBox(__datatableTemperature);
        }


        public void StopMeasurement()
        {
            runningTask = null;
            if (myTask != null)
                myTask.Dispose();
            dataWriteFile.Close();
        }


        public void StartMeasurement()
        {
            try
            {
                myTask = new NationalInstruments.DAQmx.Task();
                myAsyncCallback = new AsyncCallback(AnalogInCallback);

                // tell the PXIe-4357 to use the channels in channelsToUse
                foreach (string channel in channelsToUseAddresses)
                {
                    myTask.AIChannels.CreateRtdChannel(channel, "", __minimumValueNumeric, __maximumValueNumeric,
                        __temperatureUnit, __rtdType, __resistanceConfiguration, __excitationSource,
                        __currentExcitationNumeric, __r0Numeric);
                }

                // tell the PXIe-4357 to use its internal sample clock at some sample rate
                myTask.Timing.ConfigureSampleClock("", __sampleRate, SampleClockActiveEdge.Rising,
                    SampleQuantityMode.ContinuousSamples, __samplesPerChannelBeforeRelease);

                // check if the PXIe-4357 likes our settings
                myTask.Control(TaskAction.Verify);

                // declare the reader object for the task and prepare to run the task
                analogInReader = new AnalogMultiChannelReader(myTask.Stream);
                runningTask = myTask;

                // Use SynchronizeCallbacks to specify that the object 
                // marshals callbacks across threads appropriately.
                analogInReader.SynchronizeCallbacks = true;
                analogInReader.BeginReadWaveform(__samplesPerChannelBeforeRelease, myAsyncCallback, myTask);
            }
            catch (DaqException exception)
            {
                ErrorCatcher(exception);
            }
        }


        // function called by async when data comes in from the PXIe-4357
        // basically it just reads and then writes to the file
        private void AnalogInCallback(IAsyncResult ar)
        {
            try
            {
                if (runningTask != null && runningTask == ar.AsyncState)
                {
                    AnalogWaveform<double>[] data = analogInReader.EndReadWaveform(ar);
                    DataWrite(data);
                    analogInReader.BeginMemoryOptimizedReadWaveform(__samplesPerChannelBeforeRelease, myAsyncCallback, myTask, data);
                }
            }
            catch (DaqException exception)
            {
                ErrorCatcher(exception);
            }
        }


        // convert the data from the AnalogWaveform to a string to write
        private void DataWrite(AnalogWaveform<double>[] sourceArray)
        {
            try
            {
                if (sourceArray.Length != channelsToUseAddresses.Count)
                    throw new System.DataMisalignedException("Error: the incoming data has a different size than the number of channels!");

                string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
                string currentTime = DateTime.Now.ToString("hh:mm:ss.fff");
                DataRow dataRow = __datatableTemperature.NewRow();
                dataRow[0] = currentDate;
                dataRow[1] = currentTime;

                // assume sourceArray has channels in the original order
                foreach (int sample in Enumerable.Range(0, __samplesPerChannelBeforeRelease))
                {
                    string currentLine = currentDate + "," + currentTime + ",";
                    double[] sampleData = new double[__channelsToUse.Count];
                    
                    foreach (int channel in __channelsToUse)
                    {
                        sampleData[channel] = sourceArray[channel].Samples[sample].Value;
                        dataRow[channel.ToString()] = sourceArray[channel].Samples[sample].Value.ToString("N2");
                    }
                    currentLine += String.Join(",", sampleData);
                    dataWriteFile.WriteLine(currentLine);

                    __datatableTemperature.Rows.Add(dataRow);
                    mainRef.addLineToTemperatureBox(__datatableTemperature);
                }
            }
            catch (System.DataMisalignedException dmex)
            {
                System.Windows.MessageBox.Show(dmex.Message);
            }
        }


        // generic error handler to display any messages from DAQmx
        private void ErrorCatcher(DaqException exception)
        {
            System.Windows.MessageBox.Show(exception.Message);
            myTask.Dispose();
            runningTask = null;
        }
    }
}

﻿/* Copyright 2016 by Northwestern University
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
        private SwitchMatrixController swCont;
        private System.Windows.Threading.DispatcherTimer elapsedTimer;
        private DateTime startDateTime;
        private ImpedanceMeasurementController impMeasCont;
        private List<Dictionary<string, List<string>>> measurementPermutations;
        private List<Dictionary<string, List<string>>> ablationPermutations;

        public MainWindow()
        {
            InitializeComponent();
            CheckHardwareStatus();
            impMeasCont = new ImpedanceMeasurementController();
            //InitializeControllers();
        }


        private void CheckHardwareStatus()
        {
            // check for switches, which should be the matrix switch
            ModularInstrumentsSystem modularInstrumentsSystem = new ModularInstrumentsSystem("NI-SWITCH");
            if (modularInstrumentsSystem.DeviceCollection.Count == 1 && modularInstrumentsSystem.DeviceCollection[0].Model == "NI PXIe-2529")
            {
                labelSwitchConnectionStatus.Content = "PXIe-2529 128-Connection OK";
                labelSwitchConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            else
            {
                labelSwitchConnectionStatus.Content = "Problem with PXIe-2529 128-Connection";
                labelSwitchConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            }

            // check for DAQs, which should be temperature system
            var daq_channels = DaqSystem.Local.GetPhysicalChannels(PhysicalChannelTypes.AI, PhysicalChannelAccess.External);
            if (daq_channels.Count() >= 20)
            {
                labelTemperatureConnectionStatus.Content = "PXIe-4357 20-Channel OK";
                labelTemperatureConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            else
            {
                labelTemperatureConnectionStatus.Content = "Problem with PXIe-4357 20-Channel";
                labelTemperatureConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            }

            // check for HM8118 via NI-VISA *IDN?\r command
            lcrMeterCont = new LcrMeterController();
            if (lcrMeterCont.IsEnabled == true && lcrMeterCont.TestConnection() == true)
            {
                labelImpedanceConnectionStatus.Content = "HM8118 VISA OK";
                labelImpedanceConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
            }
            else
            {
                labelImpedanceConnectionStatus.Content = "Problem with HM8118 VISA";
                labelImpedanceConnectionStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            }
        }


        private void InitializeSwitchAndTemperatureControllers(string saveFileLocationFolder)
        {
            string saveFileLocation = saveFileLocationFolder + "/" + DateTime.Now.ToString("yyyy.MM.dd") + "-" + DateTime.Now.ToString("HH.mm");
            tempMeasCont = new TemperatureMeasurementController(saveFileLocation);
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
                if (textboxFolderPath.Text != "")
                {
                    InitializeSwitchAndTemperatureControllers(textboxFolderPath.Text);
                    buttonInitializeControllers.IsEnabled = false;
                    buttonStartCollection.IsEnabled = true;
                    labelControllerStatus.Content = "Initialized";
                }
                else
                    System.Windows.MessageBox.Show("You forgot to choose a path!");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.ToString());
            }
        }


        private void buttonStartCollection_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont.StartMeasurement();
            startDateTime = DateTime.Now;
            elapsedTimer = new System.Windows.Threading.DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), System.Windows.Threading.DispatcherPriority.Normal, delegate
            {
                labelTimeElapsed.Content = (DateTime.Now.Subtract(startDateTime)).ToString(@"mm\:ss");
            }, this.Dispatcher);

            // TODO: dispatcher timer for asking the ImpedanceMeasurementController to collect data, which then calls to set ablation after and resets timer

            buttonStartCollection.IsEnabled = false;
            buttonStopCollection.IsEnabled = true;
            labelControllerStatus.Content = "Running...";
        }


        private void buttonStopCollection_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont.StopMeasurement();
            elapsedTimer.IsEnabled = false;
            buttonStopCollection.IsEnabled = false;
            buttonFlushSystem.IsEnabled = true;
            labelControllerStatus.Content = "Stopped";
        }


        private void buttonFlushSystem_Click(object sender, RoutedEventArgs e)
        {
            tempMeasCont = null;
            buttonInitializeControllers.IsEnabled = true;
            buttonFlushSystem.IsEnabled = false;
            labelControllerStatus.Content = "Flushed";
        }
    }


    public class SwitchMatrixController
    {
        private NISwitch switchSession;
        private List<Dictionary<string,List<string>>> impedanceSwitchGroups; // groups to wire together, either single pairs, dual pairs, or full sides; 21 on device, 4 external
        private List<Dictionary<string, List<string>>> ablationSwitchGroups; // groups to wire together, either single pairs, dual pairs, or full sides; 21 on device, 4 external
        private int impedanceCounter = 0;
        private int ablationCounter = 0;

        static private string __switchAddress = "PXI1Slot2";
        static private string __switchTopology = "2529/2-Wire 4x32 Matrix";
        static private string __lcrMeterPositive = "r0";
        static private string __lcrMeterNegative = "r1";
        static private string __rfGeneratorPositive = "r2";
        static private string __rfGeneratorNegative = "r3";
        static private string __rfGeneratorSwitch = "c31";
        static private PrecisionTimeSpan maxTime = new PrecisionTimeSpan(5);

        public SwitchMatrixController(List<Dictionary<string, List<string>>> impGroups, List<Dictionary<string, List<string>>> ablGroups)
        {
            try
            {
                switchSession = new NISwitch(__switchAddress, __switchTopology, false, true);
                switchSession.DriverOperation.Warning += new System.EventHandler<SwitchWarningEventArgs>(DriverOperationWarning);
                impedanceSwitchGroups = impGroups;
                ablationSwitchGroups = ablGroups;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SwitchMatrixController() failure");
                throw new Exception("Error: Could not create SwitchMatrixController!");
            }
        }


        public Dictionary<string, List<string>> GetNextImpedancePermutation()
        {
            if (impedanceCounter + 1 >= impedanceSwitchGroups.Count)
                return impedanceSwitchGroups[0];
            return impedanceSwitchGroups[impedanceCounter + 1];
        }


        public bool UseNextImpedancePermutation()
        {
            try
            {
                switchSession.Path.DisconnectAll();
                ++impedanceCounter;
                if (impedanceCounter >= impedanceSwitchGroups.Count)
                    impedanceCounter = 0;
                string connectionList = "";
                foreach (string channel in impedanceSwitchGroups[impedanceCounter]["Positive"])
                {
                    connectionList += __lcrMeterPositive + "->" + channel + ",";
                }
                switchSession.Path.ConnectMultiple(connectionList);
                connectionList = "";
                foreach (string channel in impedanceSwitchGroups[impedanceCounter]["Negative"])
                {
                    connectionList += __lcrMeterNegative + "->" + channel + ",";
                }
                switchSession.Path.ConnectMultiple(connectionList);
                // Wait for any relay to activate and debounce.
                switchSession.Path.WaitForDebounce(maxTime);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "SwitchMatrixController.UseNextImpedancePermutation() failure");
                return false;
            }
        }


        public Dictionary<string, List<string>> GetNextAblationPermutation()
        {
            if (ablationCounter + 1 >= ablationSwitchGroups.Count)
                return ablationSwitchGroups[0];
            return ablationSwitchGroups[ablationCounter + 1];
        }


        public bool UseNextAblationPermutation()
        {
            try
            {
                switchSession.Path.DisconnectAll();
                ++ablationCounter;
                if (ablationCounter >= ablationSwitchGroups.Count)
                    ablationCounter = 0;
                string connectionList = "";
                foreach (string channel in ablationSwitchGroups[impedanceCounter]["Positive"])
                {
                    connectionList += __rfGeneratorPositive + "->" + channel + ",";
                }
                switchSession.Path.ConnectMultiple(connectionList);
                connectionList = "";
                foreach (string channel in ablationSwitchGroups[impedanceCounter]["Negative"])
                {
                    connectionList += __rfGeneratorNegative + "->" + channel + ",";
                }
                switchSession.Path.ConnectMultiple(connectionList);
                // Wait for any relay to activate and debounce.
                switchSession.Path.WaitForDebounce(maxTime);
                // connect c31 to __rfGeneratorNegative to turn it all on
                switchSession.Path.Connect(__rfGeneratorNegative, __rfGeneratorSwitch);
                switchSession.Path.WaitForDebounce(maxTime);

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
        private List<Dictionary<string, List<string>>> impedanceSwitchGroups; // TODO: groups to wire together, either single pairs, dual pairs, or full sides; 21 on device, 4 external
        private List<Dictionary<string, List<string>>> ablationSwitchGroups; // TODO: groups to wire together, either single pairs, dual pairs, or full sides; 21 on device, 4 external
        private SwitchMatrixController swMatCont;


        static private string __dataTableHeader;
        static private List<string> __groupN = new List<string> { "c0", "c1", "c2", "c3" };
        static private List<string> __groupE = new List<string> { "c4", "c5", "c6", "c7" };
        static private List<string> __groupS = new List<string> { "c8", "c9", "c10", "c11" };
        static private List<string> __groupW = new List<string> { "c12", "c13", "c14", "c15" };
        static private List<string> __groupB = new List<string> { "c16", "c17", "c18", "c19" };
        static private List<string> __groupT = new List<string> { "c20" };
        static private List<string> __groupX = new List<string> { "c21" };
        static private List<string> __groupY = new List<string> { "c22" };
        static private List<string> __groupZ = new List<string> { "c23" };
        static private List<string> __groupAllInternal = __groupN.Concat(__groupE).Concat(__groupS).Concat(__groupW).Concat(__groupB).Concat(__groupT).ToList();
        static private List<string> __externalElectrodes = new List<string> { "X", "Y", "Z" };
        static private List<string> __internalElectrodes = new List<string> { "AllInternal", "N", "E", "S", "W", "B", "T" };


        public ImpedanceMeasurementController()
        {
            impedanceSwitchGroups = new List<Dictionary<string, List<string>>>();
            foreach (string extCode in __externalElectrodes)
            {
                foreach (string intCode in __internalElectrodes)
                {
                    impedanceSwitchGroups.Add(ConvertPositiveNegativeFaceCodeToPermutation(intCode, extCode));
                }
            }

            ablationSwitchGroups = new List<Dictionary<string, List<string>>>();
            swMatCont = new SwitchMatrixController(impedanceSwitchGroups, ablationSwitchGroups);
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
            Dictionary<string, List<string>> returnDict = new Dictionary<string, List<string>> { { "Positive", posColumns}, {"Negative", negColumns } };
            return returnDict;
        }


        // TODO: Loop through the impedance measurement groups and then write its impedance value to file with timestamp
        private bool CollectImpedanceMeasurements()
        {
            return false;
        }

    }


    public class LcrMeterController
    {
        private NationalInstruments.Visa.ResourceManager rmSession;
        private NationalInstruments.Visa.MessageBasedSession mbSession;
        public bool IsEnabled;

        static private string __lcrMeterPort = "ASRL5::INSTR";
        static private int __lcrFrequency = 100000;
        static private string __termchar = "\r";

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
        // TODO: Maybe sync with the impedance reader so a temperature reading occurs the same time as a impedance measurement 
        private AnalogMultiChannelReader analogInReader;
        private AsyncCallback myAsyncCallback;
        private NationalInstruments.DAQmx.Task myTask;
        private NationalInstruments.DAQmx.Task runningTask;
        private List<string> channelsToUseAddresses = new List<string>();
        private System.IO.StreamWriter dataWriteFile;

        // RTD physical configuration
        static private List<int> __channelsToUse = new List<int>(Enumerable.Range(0, 20)); // use all of the channels
        static private string __pxiLocation = "PXI1Slot3";
        static private AIRtdType __rtdType = AIRtdType.Pt3851;
        static private double __r0Numeric = 100.0;
        static private AIResistanceConfiguration __resistanceConfiguration = AIResistanceConfiguration.ThreeWire;
        static private AIExcitationSource __excitationSource = AIExcitationSource.Internal;
        static private double __minimumValueNumeric = 0.0;
        static private double __maximumValueNumeric = 200.0;
        static private AITemperatureUnits __temperatureUnit = AITemperatureUnits.DegreesC;
        static private double __currentExcitationNumeric = 900e-6;

        // Sampling configuration
        static private double __sampleRate = 2.0; // in Hz
        static private int __samplesPerChannelBeforeRelease = 1;

        // Data table configuration
        static private string __dataTableHeader; 


        public TemperatureMeasurementController(string saveFileLocation)
        {
            // the USB daq uses "cDAQ1Mod1/aiX" as its location
            foreach (int channelId in __channelsToUse)
                channelsToUseAddresses.Add(__pxiLocation + "/ai" + channelId.ToString());
            dataWriteFile = new System.IO.StreamWriter(saveFileLocation+".temperature.csv");
            __dataTableHeader = "date,time," + String.Join(",", channelsToUseAddresses);
            dataWriteFile.WriteLine(__dataTableHeader);
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

                // assume sourceArray has channels in the original order
                foreach (int sample in Enumerable.Range(0, __samplesPerChannelBeforeRelease))
                {
                    string currentLine = currentDate + "," + currentTime + ",";
                    double[] sampleData = new double[__channelsToUse.Count];
                    foreach (int channel in __channelsToUse)
                    {
                        sampleData[channel] = sourceArray[channel].Samples[sample].Value;
                    }
                    currentLine += String.Join(",", sampleData);
                    dataWriteFile.WriteLine(currentLine);
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

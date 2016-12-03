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
using System.Data;
using System.Windows;
using System.Windows.Data;
using System.Threading;

using NationalInstruments;
using NationalInstruments.ModularInstruments.NISwitch;
using NationalInstruments.ModularInstruments.SystemServices.DeviceServices;
using NationalInstruments.Visa;
using NationalInstruments.DAQmx;

using NetMQ;

namespace NsfSwitchControl
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private TemperatureMeasurementController tempMeasCont;
		private TemperatureMeasurementController tempMeasContTop;
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
			impMeasCont = new ImpedanceMeasurementController(saveFileLocation, this);
			tempMeasCont = new TemperatureMeasurementController(saveFileLocation, this, "PXI1Slot2");
			tempMeasContTop = new TemperatureMeasurementController(saveFileLocation, this, "cDAQ1Mod1");
			listBoxFirstAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
			listBoxSecondAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
			listBoxThirdAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
			listBoxFourthAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
			listBoxFifthAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
			listBoxLastAblationSide.ItemsSource = impMeasCont.GetPossibleAblationSides();
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
				InitializeSwitchAndTemperatureControllers(textboxFolderPath.Text);
				buttonInitializeControllers.IsEnabled = false;
				buttonStartCollection.IsEnabled = true;
				labelControllerStatus.Content = "Initialized";
				labelControllerStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(ex.ToString());
			}
		}


		private void buttonStartCollection_Click(object sender, RoutedEventArgs e)
		{
			if (textboxFolderPath.Text != "" && int.Parse(textboxImpMeasSamplesDesired.Text) >= 2)
			{
				int impedanceMeasurementInterval = int.Parse(textboxImpMeasIntervalDesired.Text);
				int totalNumberOfImpMeasSamples = int.Parse(textboxImpMeasSamplesDesired.Text);
				impMeasCont.SetUseExternalElectrodes(checkBoxExternalElectrodes.IsChecked);

				string firstAblationSides = String.Join(",", listBoxFirstAblationSide.SelectedItems.OfType<string>().ToList());
				string secondAblationSides = String.Join(",", listBoxSecondAblationSide.SelectedItems.OfType<string>().ToList());
				string thirdAblationSides = String.Join(",", listBoxThirdAblationSide.SelectedItems.OfType<string>().ToList());
				string fourthAblationSides = String.Join(",", listBoxFourthAblationSide.SelectedItems.OfType<string>().ToList());
				string fifthAblationSides = String.Join(",", listBoxFifthAblationSide.SelectedItems.OfType<string>().ToList());
				string lastAblationSides = String.Join(",", listBoxLastAblationSide.SelectedItems.OfType<string>().ToList());
				List<string> finalAblationList = new List<string> { firstAblationSides, secondAblationSides, thirdAblationSides, fourthAblationSides, fifthAblationSides, lastAblationSides };

				impMeasCont.SetActiveAblationSides(finalAblationList);
				impMeasCont.SetBaseAblationCountLimits(totalNumberOfImpMeasSamples);
				impMeasCont.SetBaseAblationDurations(impedanceMeasurementInterval);

				tempMeasCont.StartMeasurement();
				if (tempMeasContTop != null)
					tempMeasContTop.StartMeasurement();
				impMeasCont.StartCollection();
				startDateTime = DateTime.Now;
				elapsedTimer = new System.Windows.Threading.DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), System.Windows.Threading.DispatcherPriority.Normal, delegate
				{
					labelTimeElapsed.Content = (DateTime.Now.Subtract(startDateTime)).ToString(@"mm\:ss") + ", Ablation Group: " + impMeasCont.GroupsAblating() + ", Counts Taken: " + impMeasCont.SamplesTaken();
					if (impMeasCont.IsComplete())
						StopCollection(true);
				}, this.Dispatcher);
				buttonStartCollection.IsEnabled = false;
				buttonStopCollection.IsEnabled = true;
				labelControllerStatus.Content = "Running...";
			}
			else
				System.Windows.MessageBox.Show("You forgot to choose a path or the number of samples is less than 2!");
		}


		private void StopCollection(bool isComplete)
		{
			tempMeasCont.StopMeasurement();
			if (tempMeasContTop != null)
				tempMeasContTop.StopMeasurement();
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
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(rebindDatagrid), dtForGrid, datagridImpedance);
			}
			else
			{
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(scrollDatagrid), dtForGrid, datagridImpedance);
			}
		}

		public void addLineToTemperatureBox(DataTable dtForGrid)
		{
			if (__dtTempName != dtForGrid.TableName)
			{
				__dtTempName = dtForGrid.TableName;
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(rebindDatagrid), dtForGrid, datagridTemperature);
			}
			else
			{
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new TextBoxUpdateDelegate(scrollDatagrid), dtForGrid, datagridTemperature);
			}
		}

		private void rebindDatagrid(DataTable dtForGrid, System.Windows.Controls.DataGrid theDataGrid)
		{
			theDataGrid.ItemsSource = dtForGrid.DefaultView;
			theDataGrid.AutoGenerateColumns = true;
		}

		private void scrollDatagrid(DataTable dtForGrid, System.Windows.Controls.DataGrid theDataGrid)
		{
			try
			{
				theDataGrid.Items.Refresh();
				int count = theDataGrid.Items.Count;
				if (theDataGrid.Items.Count > 0)
					theDataGrid.ScrollIntoView(theDataGrid.Items.GetItemAt(theDataGrid.Items.Count - 1));
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "datagrid problem!");
			}
		}

		private void selectAllTextInTextbox(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
		{
			var tb = (System.Windows.Controls.TextBox)sender;
			tb.SelectAll();
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


	public class LcrMeterController
	{
		private NationalInstruments.Visa.ResourceManager rmSession;
		private NationalInstruments.Visa.MessageBasedSession mbSession;
		public bool IsEnabled;

		private const string __lcrMeterPort = "ASRL4::INSTR";
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


		public bool TestConnection()
		{
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
			for (int i = 0; i < 100; ++i)
			{
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
		public string GetRawZThetaString()
		{
			try
			{
				mbSession.RawIO.Write("XALL?" + __termchar);
				string garbage = mbSession.RawIO.ReadString();
				while (garbage == "ERROR" + __termchar)
				{
					mbSession.RawIO.Write("XALL?" + __termchar);
					garbage = mbSession.RawIO.ReadString();
				}
				return garbage;
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


		public Tuple<string, string> GetFormattedZThetaStrings()
		{
			while (!this.IsLCRMeterReady()) { };
			string lcrXallIn = GetRawZThetaString();
			string[] splitString = lcrXallIn.Split(',');
			return new Tuple<string, string>(splitString[0], splitString[1]);
		}


		public bool SetLcrMeterFrequency(int desiredFreq)
		{
			try
			{
				mbSession.RawIO.Write("FREQ " + desiredFreq.ToString() + __termchar);
				mbSession.RawIO.Write("FREQ?" + __termchar);
				if (mbSession.RawIO.ReadString() == desiredFreq.ToString() + "\r")
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


	public class DummyLcrMeterController
	{
		public bool IsEnabled;

		private const string __lcrMeterPort = "ASRL3::INSTR";
		private const int __lcrFrequency = 100000;
		private const string __termchar = "\r";

		public DummyLcrMeterController()
		{
			this.SetLcrMeterFrequency(__lcrFrequency);
			IsEnabled = true;
		}


		public bool TestConnection()
		{
			return true;
		}


		// busy wait to check if LCR meter
		public bool IsLCRMeterReady()
		{
			return true;
		}


		// remember to check if LCR meter is ready first
		public string GetZThetaValue()
		{
			Thread.Sleep(1000);
			return "999,999";
		}


		public bool SetLcrMeterFrequency(int desiredFreq)
		{
			return true;
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

		public static object _syncLock = new object();

		// RTD physical configuration
		private string __pxiLocation;
		private readonly AIRtdType __rtdType = AIRtdType.Pt3851;
		private const double __r0Numeric = 100.0;
		private readonly AIResistanceConfiguration __resistanceConfiguration = AIResistanceConfiguration.ThreeWire;
		private readonly AIExcitationSource __excitationSource = AIExcitationSource.Internal;
		private readonly double __minimumValueNumeric = 0.0;
		private readonly double __maximumValueNumeric = 200.0;
		private readonly AITemperatureUnits __temperatureUnit = AITemperatureUnits.DegreesC;
		private readonly double __currentExcitationPXI = 900e-6;
		private readonly double __currentExcitationUSB = 1e-3;

		// Sampling configuration
		private const double __sampleRate = 2.0; // in Hz
		private const int __samplesPerChannelBeforeRelease = 1;

		// Data table configuration
		private string __dataTableHeader;
		private DateTime __recordingStartTime;
		private MainWindow mainRef;
		private DataTable __datatableTemperature = new DataTable("temperature");

		private readonly List<int> __stickN = new List<int> { 0, 5, 10, 15 };
		private readonly List<int> __stickE = new List<int> { 1, 6, 11, 16 };
		static private readonly List<int> __stickS = new List<int> { 2, 7, 12, 17 };
		static private readonly List<int> __stickW = new List<int> { 3, 8, 13, 18 };
		static private readonly List<int> __stickB = new List<int> { 4, 9, 14, 19 };
		static private readonly List<int> __stickT = new List<int> { 0, 1, 2, 3 };

		// the usb daq also stores in a different file anyway
		private List<List<int>> __sticksToMeasure;
		private List<int> __channelsToUse;
		private bool isUSB = false;

		public TemperatureMeasurementController(string saveFileLocation, MainWindow mainRefIn, string devAddress)
		{
			__pxiLocation = devAddress;
			// the USB daq uses "cDAQ1Mod1/aiX" as its location
			__datatableTemperature.Columns.Add("Date");
			__datatableTemperature.Columns.Add("Time");
			//foreach (int channelId in __channelsToUse)
			if (devAddress[0] == 'P')
			{
				dataWriteFile = new System.IO.StreamWriter(saveFileLocation + ".temperature.csv");
				__sticksToMeasure = new List<List<int>> { __stickN, __stickE, __stickS, __stickW, __stickB };
			}
			else
			{
				dataWriteFile = new System.IO.StreamWriter(saveFileLocation + ".temperature-usb.csv");
				__sticksToMeasure = new List<List<int>> { __stickT };
				isUSB = true;
			}
			__channelsToUse = __sticksToMeasure.SelectMany(x => x).ToList();
			foreach (List<int> stickList in __sticksToMeasure)
			{
				foreach (int channelId in stickList)
				{
					channelsToUseAddresses.Add(__pxiLocation + "/ai" + channelId.ToString());
					__datatableTemperature.Columns.Add(ConvertChannelIdToFace(channelId) + channelId.ToString());
				}
			}
			__dataTableHeader = "date,time," + String.Join(",", channelsToUseAddresses);
			dataWriteFile.WriteLine(__dataTableHeader);

			if (devAddress[0] == 'P')
			{
				BindingOperations.EnableCollectionSynchronization(__datatableTemperature.DefaultView, _syncLock);
				mainRef = mainRefIn;
				mainRef.addLineToTemperatureBox(__datatableTemperature);
			}
		}


		private string ConvertChannelIdToFace(int channelId)
		{
			if (__stickN.Contains(channelId))
			{
				return "N";
			}
			else if (__stickE.Contains(channelId))
			{
				return "E";
			}
			else if (__stickS.Contains(channelId))
			{
				return "S";
			}
			else if (__stickW.Contains(channelId))
			{
				return "W";
			}
			else if (__stickB.Contains(channelId))
			{
				return "B";
			}
			else
			{
				return "?";
			}
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
				double __currentExcitationNumeric;
				if (__pxiLocation[0] == 'P')
					__currentExcitationNumeric = __currentExcitationPXI;
				else
					__currentExcitationNumeric = __currentExcitationUSB;

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

				__recordingStartTime = DateTime.Now;

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
				string currentTime = (DateTime.Now - __recordingStartTime).TotalSeconds.ToString();
				DataRow dataRow = __datatableTemperature.NewRow();
				dataRow[0] = currentDate;
				dataRow[1] = currentTime;

				// assume sourceArray has channels in the original order
				foreach (int sample in Enumerable.Range(0, __samplesPerChannelBeforeRelease))
				{
					string currentLine = currentDate + "," + currentTime + ",";
					double[] sampleData = new double[__channelsToUse.Count];

					// sampleData is in direct order, sourceArray is in added order
					// so __channelsToUse is going to be in 0,5,10,15
					// but sourceArray is ordered to be the original, so we have to get the actual index
					foreach (int channel in Enumerable.Range(0, __channelsToUse.Count)) //__channelsToUse) 
					{
						sampleData[__channelsToUse[channel]] = sourceArray[channel].Samples[sample].Value;
						dataRow[ConvertChannelIdToFace(__channelsToUse[channel]) + __channelsToUse[channel].ToString()] = sourceArray[channel].Samples[sample].Value.ToString("N2");
					}
					currentLine += String.Join(",", sampleData);
					dataWriteFile.WriteLine(currentLine);

					if (!isUSB)
					{
						lock (_syncLock)
						{
							__datatableTemperature.Rows.Add(dataRow);
						}
						mainRef.addLineToTemperatureBox(__datatableTemperature);
					}
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


	public class SingleImpedanceMeasurement
	{
		public string side;
		public string pos;
		public string neg;
		public string time;
		public string magn;
		public string phase;
		public string joined;

		public SingleImpedanceMeasurement(string in_side, string in_pos, string in_neg, string in_time, string in_magn, string in_phase)
		{
			side = in_side;
			pos = in_pos;
			neg = in_neg;
			time = in_time;
			magn = in_magn;
			phase = in_phase;
			List<string> all_together = new List<string> { time, pos, neg, magn, phase };
			joined = String.Join(",", all_together);
		}

		public SingleImpedanceMeasurement(DataRow inData)
		{
			if (inData.ItemArray.Count() < 6)
				throw new ValueUnavailableException();
			else
			{
				string negCode = (string)inData[3];
				if (negCode.Length == 3)
					side = (string)inData[2];
				else
					side = (string)inData[3];
				pos = (string)inData[2];
				neg = (string)inData[3];
				time = (string)inData[1];
				magn = (string)inData[4];
				phase = (string)inData[5];
				List<string> all_together = new List<string> { time, pos, neg, magn, phase };
				joined = String.Join(",", all_together);
			}
		}

		public override string ToString()
		{
			return joined;
		}
	}


	public class AblationGroup
	{
		public List<string> activeSides;
		public int activeDuration;
		public int countUsage;
		public int countLimit = 0; // 0 if using the flag directly, otherwise we disable after a count
		public List<string> posElectrodes;
		public List<string> negElectrodes;
		public bool active = false;
		public bool isComplete = false;

		public Dictionary<string, List<string>> ToDictionary()
		{
			return new Dictionary<string, List<string>>
			{
				{"Positive", posElectrodes },
				{"Negative", negElectrodes }
			};
		}
	}


	public class ImpedanceMeasurementController
	{
		private System.IO.StreamWriter dataWriteFile;
		private DateTime __recordingStartTime;
		private DataTable __datatableImpedance = new DataTable("impedance");

		private MainWindow mainRef;
		private SwitchMatrixController swMatCont;
		private LcrMeterController lcrMeterCont;
		private System.Threading.Timer collectionTimer;
		private System.Threading.TimerCallback collectCallback;
		public static object _syncLock = new object();

		private bool useExternalElectrodes = false;
		private const int __preAblationMilliseconds = 5000;
		private Random rseed = new Random();

		static private Dictionary<string, Tuple<string, List<string>, List<string>>> __electrodeGroups = new Dictionary<string, Tuple<string, List<string>, List<string>>>
		{
			// each side has its opposite, its connections, its opp-connections
			{"N", new Tuple<string, List<string>, List<string>>("T.E.B.W", new List<string>{ "c0", "c1", "c2", "c3" }, new List<string>() ) },
			{"E", new Tuple<string, List<string>, List<string>>("T.N.B.S", new List<string>{ "c4", "c5", "c6", "c7" }, new List<string>() ) },
			{"S", new Tuple<string, List<string>, List<string>>("T.E.B.W", new List<string>{ "c8", "c9", "c10", "c11" }, new List<string>() ) },
			{"W", new Tuple<string, List<string>, List<string>>("T.N.B.S", new List<string>{ "c12", "c13", "c14", "c15" }, new List<string>() ) },
			{"B", new Tuple<string, List<string>, List<string>>("N.E.S.W", new List<string>{ "c16", "c17", "c18", "c19" }, new List<string>() ) },
			{"T", new Tuple<string, List<string>, List<string>>("N.E.S.W", new List<string>{ "c20" } , new List<string>() ) },
			{"N-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c0", "c1", "c2", "c3" }, new List<string>{ "c24" } ) },
			{"E-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c4", "c5", "c6", "c7" }, new List<string>{ "c24" } ) },
			{"S-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c8", "c9", "c10", "c11" }, new List<string>{ "c24" } ) },
			{"W-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c12", "c13", "c14", "c15" }, new List<string>{ "c24" } ) },
			{"B-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c16", "c17", "c18", "c19" }, new List<string>{ "c24" } ) },
			{"T-ENE", new Tuple<string, List<string>, List<string>>("ENE", new List<string>{ "c20" } , new List<string>{ "c24" } ) },
		};

		private class DepthController
		{
			private NetMQ.Sockets.RequestSocket socket;
			private System.Collections.Concurrent.ConcurrentQueue<DataRow> IMCQueue;
			private Dictionary<string, List<SingleImpedanceMeasurement>> initialMeasurementLists = new Dictionary<string, List<SingleImpedanceMeasurement>>
		    {
			    {"N", new List<SingleImpedanceMeasurement>() },
			    {"E", new List<SingleImpedanceMeasurement>() },
			    {"S", new List<SingleImpedanceMeasurement>() },
			    {"W", new List<SingleImpedanceMeasurement>() },
			    {"B", new List<SingleImpedanceMeasurement>() },
			    {"T", new List<SingleImpedanceMeasurement>() },
		    };
			    private Dictionary<string, SingleImpedanceMeasurement> initialMeasurements = new Dictionary<string, SingleImpedanceMeasurement>();
			    private Dictionary<string, SingleImpedanceMeasurement> currentMeasurements = new Dictionary<string, SingleImpedanceMeasurement>();
			    private Dictionary<string, List<SingleImpedanceMeasurement>> currentMeasurementLists = new Dictionary<string, List<SingleImpedanceMeasurement>>
		    {
			    {"N", new List<SingleImpedanceMeasurement>() },
			    {"E", new List<SingleImpedanceMeasurement>() },
			    {"S", new List<SingleImpedanceMeasurement>() },
			    {"W", new List<SingleImpedanceMeasurement>() },
			    {"B", new List<SingleImpedanceMeasurement>() },
			    {"T", new List<SingleImpedanceMeasurement>() },
		    };
			    private Dictionary<string, double> currentDepths = new Dictionary<string, double>
		    {
			    {"N", 0.0},
			    {"E", 0.0},
			    {"S", 0.0},
			    {"W", 0.0},
			    {"B", 0.0},
			    {"T", 0.0}
		    };

			private System.Threading.Timer collectionTimer;
			private bool collectData = false;
			private bool initialMeasurementsCalculated = false;
			private object _syncLock = new object();
			private bool usingExternalElectrodes;
			private enum measurementState { PreInitial, Initial, InAblation };
			private measurementState currentState = measurementState.PreInitial;
			private int numberOfMeasurements = 0;
			private int __nominalCount;
			private ImpedanceMeasurementController imcLink;

			public DepthController(System.Collections.Concurrent.ConcurrentQueue<DataRow> InputQueue, bool usingExternal, string svmServerAddress, ImpedanceMeasurementController theIMC)
			{
				socket = new NetMQ.Sockets.RequestSocket(svmServerAddress);
				IMCQueue = InputQueue;
				usingExternalElectrodes = usingExternal;
				imcLink = theIMC;
				if (usingExternalElectrodes)
					__nominalCount = 30; // 5 measurements per measurement block, extra 5 measurements per measurement block, 3 blocks per measurement instance
				else
					__nominalCount = 15;
			}

			public bool Start()
			{
				collectData = true;
				collectionTimer = new System.Threading.Timer(UpdateDepth, null, 0, System.Threading.Timeout.Infinite);
				return false;
			}


			public bool Stop()
			{
				collectData = false;
				return false;
			}

			public void SetTargetDepth(string side, double depth)
			{
				imcLink.targetDepths[side] = depth;
			}

			public void UpdateDepth(object noobject)
			{
				if (!IMCQueue.IsEmpty)
				{
					DataRow inData;
					List<SingleImpedanceMeasurement> incomingMeasurements = new List<SingleImpedanceMeasurement>();
					while (IMCQueue.TryDequeue(out inData))
					{ // grab all we can greedily
						incomingMeasurements.Add(new SingleImpedanceMeasurement(inData));
					}
					numberOfMeasurements += incomingMeasurements.Count;

					if (numberOfMeasurements < __nominalCount)
						currentState = measurementState.PreInitial;
					else if (numberOfMeasurements < (2 * __nominalCount))
					{
						currentState = measurementState.Initial;
						foreach (SingleImpedanceMeasurement meas in incomingMeasurements)
						{
							initialMeasurementLists[meas.side].Add(meas);
						}
					}
					else // we are past the initial counts!
					{
						currentState = measurementState.InAblation;
						if (!initialMeasurementsCalculated) // we need to compute the initial measurements the first time around
						{
							foreach (KeyValuePair<string, List<SingleImpedanceMeasurement>> entry in initialMeasurementLists)
							{
								initialMeasurements[entry.Key] = DiscardAndAverage(entry.Value);
							}
						}
						else
						{
							// place the incoming measurement in the correct place
							foreach (SingleImpedanceMeasurement incomingMeasurement in incomingMeasurements)
							{
								currentMeasurementLists[incomingMeasurement.neg].Add(incomingMeasurement);
							}
							foreach (KeyValuePair<string, List<SingleImpedanceMeasurement>> measurements in currentMeasurementLists)
							{
								if (measurements.Value.Count == 3)
								{
									// We send a query of the following type: '<time>,<side>,<pos>,<magn>,<phase>,<init_magn>,<init_phase>'
									// and expect a response of the following type: '<side>,<depth>'
									List<string> outgoingQueryItems = new List<string>();
									currentMeasurements[measurements.Key] = DiscardAndAverage(measurements.Value);
									outgoingQueryItems.Add(currentMeasurements[measurements.Key].time);
									outgoingQueryItems.Add(currentMeasurements[measurements.Key].neg);
									outgoingQueryItems.Add(currentMeasurements[measurements.Key].pos);
									outgoingQueryItems.Add(currentMeasurements[measurements.Key].magn);
									outgoingQueryItems.Add(currentMeasurements[measurements.Key].phase);
									outgoingQueryItems.Add(initialMeasurements[measurements.Key].magn);
									outgoingQueryItems.Add(initialMeasurements[measurements.Key].phase);
									string outgoingQuery = String.Join(",", outgoingQueryItems);
									socket.SendFrame(outgoingQuery);

									string reply = socket.ReceiveFrameString();
									string[] replySplit = reply.Split(',');
									if (measurements.Key == replySplit[0])
										currentDepths[measurements.Key] = Double.Parse(replySplit[1]);
									else
										throw new ValueUnavailableException();

									// dump the list, since it's no longer valid (we don't want the next measurement to count)
									currentMeasurementLists[measurements.Key] = new List<SingleImpedanceMeasurement>();
								}
							}
						}
					}
				}
				else if (!collectData)
				{
					collectionTimer = null;
					return;
				}
				collectionTimer.Change(0, System.Threading.Timeout.Infinite);
			}

			public Dictionary<string, double> GetDepths()
			{
				return currentDepths;
			}

			private void UpdateAblationIntervals()
			{
				Dictionary<string, double> diffDepths = new Dictionary<string, double>();
				foreach (KeyValuePair<string, double> entry in imcLink.targetDepths)
				{
					diffDepths[entry.Key] = imcLink.targetDepths[entry.Key] - currentDepths[entry.Key];
				}

				// now that we have the diffs, we can adjust each side if we know which ablation group is in what
				foreach (AblationGroup group in imcLink.ablationSwitchGroups)
				{
					// TODO: Update intervals here, set to 0 and active to false if we're done
				}
			}

			private SingleImpedanceMeasurement DiscardAndAverage(List<SingleImpedanceMeasurement> inList)
			{
				if (inList.Count < 3)
					throw new ValueUnavailableException();
				double temp_magn = DiscardAndAverageGeneric(new List<double> { Double.Parse(inList[0].magn), Double.Parse(inList[1].magn), Double.Parse(inList[2].magn) });
				double temp_phase = DiscardAndAverageGeneric(new List<double> { Double.Parse(inList[0].phase), Double.Parse(inList[1].phase), Double.Parse(inList[2].phase) });
				double temp_temp; // TODO: have to get temperature (interpolated) inserted into this somehow later...
				return new SingleImpedanceMeasurement(inList[0].neg, inList[0].pos, inList[0].neg, inList[2].time, ((decimal)temp_magn).ToString(), ((decimal)temp_phase).ToString());
			}

			private double DiscardAndAverageGeneric(List<double> inList)
			{
				if (inList.Count < 3)
					throw new ValueUnavailableException();
				else
				{
					double diff1 = Math.Abs(inList[0] - inList[1]);
					double diff2 = Math.Abs(inList[0] - inList[2]);
					double diff3 = Math.Abs(inList[1] - inList[2]);
					if ((diff3 <= diff2) && (diff3 <= diff1))
					{
						return (inList[1] + inList[2]) / 2.0;
					}
					else if ((diff2 <= diff3) && (diff2 <= diff1))
					{
						return (inList[0] + inList[2]) / 2.0;
					}
					else if ((diff1 <= diff2) && (diff1 <= diff3))
					{
						return (inList[0] + inList[1]) / 2.0;
					}
					else
					{
						inList.Sort();
						return inList[1];
					}
				}
			}
		}

		private bool usingDepthController = false;
		private DepthController depthController;
		public System.Collections.Concurrent.ConcurrentQueue<DataRow> OutputQueue = new System.Collections.Concurrent.ConcurrentQueue<DataRow>();
		private Dictionary<string, double> targetDepths = new Dictionary<string, double>
		{
			{"N", 0.0},
			{"E", 0.0},
			{"S", 0.0},
			{"W", 0.0},
			{"B", 0.0},
			{"T", 0.0}
		};

		private enum imcStatusEnum { isStopped, preAblation, inMeasurement, inAblation, isComplete };
		private imcStatusEnum imcState = imcStatusEnum.preAblation;

		// expect ["N", "E", "S", "W", "B", "T", "N-ENE", "E-ENE", ...]
		private List<Dictionary<string, List<string>>> impedanceSwitchGroups = new List<Dictionary<string, List<string>>>();
		// this has everything we need to launch an ablation, so what we will do is actually
		// just PWM everything, this way it should work better
		// it should be a protected and friended, but oh well
		public List<AblationGroup> ablationSwitchGroups = new List<AblationGroup>();
		private AblationGroup currentAblationGroup;
		private Queue<AblationGroup> activeAblationGroupQueue = new Queue<AblationGroup>();
		private Dictionary<string, List<string>> preAblationSwitchGroup = new Dictionary<string, List<string>>
		{
			{"Positive", new List<string> { "c0", "c1", "c4", "c5", "c8", "c9", "c12", "c13", "c16", "c17", "c20" } },
			{"Negative", new List<string> { "c2", "c3", "c6", "c7", "c10", "c11", "c10", "c11", "c14", "c15", "c18", "c19" } }
		};

		public ImpedanceMeasurementController(string saveFileLocation, MainWindow mainRefIn)
		{
			lcrMeterCont = new LcrMeterController();
			swMatCont = new SwitchMatrixController();
			AddInternalElectrodesToImpedanceSwitchingGroups();
			collectCallback = new System.Threading.TimerCallback(CollectData);

			dataWriteFile = new System.IO.StreamWriter(saveFileLocation + ".impedance.csv");
			string __dataTableHeader = "date,time,pos,neg,impedance (ohms),phase (deg)";
			foreach (string col in __dataTableHeader.Split(','))
			{
				__datatableImpedance.Columns.Add(col);
			}
			dataWriteFile.WriteLine(__dataTableHeader);

			BindingOperations.EnableCollectionSynchronization(__datatableImpedance.DefaultView, _syncLock);
			mainRef = mainRefIn;
			mainRef.addLineToImpedanceBox(__datatableImpedance);
		}


		public List<string> GetPossibleAblationSides()
		{
			return new List<string> { "N", "E", "S", "W", "B", "T" };
		}


		public void SetUseDepthController()
		{
			usingDepthController = true;
			depthController = new DepthController(OutputQueue, false, ">tcp://localhost:5555", this);
		}


		private void AddImpedanceSwitchGroup(string side, Tuple<string, List<string>, List<string>> group)
		{
			Dictionary<string, List<string>> to_add = new Dictionary<string, List<string>>
			{
				{"Positive", group.Item3},
				{"Negative", group.Item2},
				{"NegativeCode", new List<string> { side } }, // the negative code should always be the side
				{"PositiveCode", new List<string>(group.Item1.Split(',')) }
			};
			impedanceSwitchGroups.Add(to_add);
		}


		private void AddInternalElectrodesToImpedanceSwitchingGroups()
		{
			foreach (KeyValuePair<string, Tuple<string, List<string>, List<string>>> entry in __electrodeGroups)
			{
				if (entry.Key.Length < 3)
				{
					AddImpedanceSwitchGroup(entry.Key, entry.Value);
				}
			}
		}


		private void AddExternalElectrodesToImpedanceSwitchingGroups()
		{
			// external-to-internal impedance measurement permutations
			foreach (KeyValuePair<string, Tuple<string, List<string>, List<string>>> entry in __electrodeGroups)
			{
				if (entry.Key.Length > 3)
					AddImpedanceSwitchGroup(entry.Key.Split('-')[0], entry.Value);
			}
		}


		public bool IsComplete()
		{
			return (imcState == imcStatusEnum.isComplete);
		}


		public void SetUseExternalElectrodes(bool? useOrNot)
		{
			bool checkedUse;
			if (useOrNot == null || useOrNot == false)
				checkedUse = false;
			else
				checkedUse = true;
			useExternalElectrodes = checkedUse;
			if (useExternalElectrodes)
				AddExternalElectrodesToImpedanceSwitchingGroups();
		}


		public void SetBaseAblationDurations(int inDuration)
		{
			int baseInterval = inDuration * 1000;
			foreach (AblationGroup group in ablationSwitchGroups)
			{
				if (group.activeSides.Count > 1)
					group.activeDuration = baseInterval;
				else // we do less time to avoid too much power deposition
					group.activeDuration = baseInterval / 10;
			}
		}


		public void SetBaseAblationCountLimits(int inLimit)
		{
			if (usingDepthController)
				throw new Exception("Cannot set initial ablation times if using depth controller!");
			foreach (AblationGroup group in ablationSwitchGroups)
			{
				if (group.activeSides.Count > 1)
					group.countLimit = inLimit;
				else // we extend the number of counts to make up for less time
					group.countLimit = 2 * inLimit;
			}
		}


		// TODO: each selector also has the targets for its side_codes
		public void SetActiveAblationSides(List<string> inAblationSides)
		{
			foreach (string side_code in inAblationSides)
			{
				// each side_code is a string that we can split using ','
				if (side_code.Length < 1)
					continue;
				string[] codes = side_code.Split(',');
				AblationGroup group = new AblationGroup();
				group.activeSides = new List<string>(codes);
				group.active = true;
				ablationSwitchGroups.Add(group);

				/* this is wrong below 
				foreach (AblationGroup group in ablationSwitchGroups)
				{
					if (group.activeSides.Contains(side_code))
					{
						group.active = true;
					}
				}
				*/
			}
		}


		public Tuple<string, int> GetCurrentAblationGroupSideAndCount()
		{
			return new Tuple<string, int>(String.Join("+", currentAblationGroup.activeSides), currentAblationGroup.countUsage);
		}


		public bool StartCollection()
		{
			imcState = imcStatusEnum.preAblation;
			__recordingStartTime = DateTime.Now;
			collectionTimer = new System.Threading.Timer(collectCallback, this, 0, System.Threading.Timeout.Infinite);
			return false;
		}


		public bool StopCollection()
		{
			imcState = imcStatusEnum.isStopped;
			swMatCont.DisconnectAll();
			return false;
		}


		private void CollectData(object stateInf)
		{
			switch (imcState)
			{
				case imcStatusEnum.preAblation:
					swMatCont.Connect(preAblationSwitchGroup, true);
					imcState = imcStatusEnum.inMeasurement;
					collectionTimer.Change(__preAblationMilliseconds, System.Threading.Timeout.Infinite);
					break;
				case imcStatusEnum.inAblation:
					AblationGroup ag_dequeued = activeAblationGroupQueue.Dequeue();
					if (imcState == imcStatusEnum.inAblation)
					{
						swMatCont.Connect(ag_dequeued.ToDictionary(), true);
						if (activeAblationGroupQueue.Count == 0)
							imcState = imcStatusEnum.inMeasurement;
						collectionTimer.Change(ag_dequeued.activeDuration, System.Threading.Timeout.Infinite);
					}
					else
					{
						dataWriteFile.Close();
						collectionTimer.Dispose();
						break;
					}
					break;
				case imcStatusEnum.inMeasurement:
					for (int i = 0; i < 3; i++)
					{
						foreach (Dictionary<string, List<string>> permutation in impedanceSwitchGroups)
						{
							if (imcState != imcStatusEnum.isStopped)
							{
								swMatCont.Connect(permutation, false);
								Tuple<string, string> impPhaseDataSeparated = lcrMeterCont.GetFormattedZThetaStrings();
								WritePermutationImpedanceToFile(permutation, impPhaseDataSeparated.Item1, impPhaseDataSeparated.Item2);
							}
							else
							{
								dataWriteFile.Close();
								collectionTimer.Dispose();
								return;
							}
						}
					}
					swMatCont.DisconnectAll();
					// check if there is anything left to ablate
					bool groupsLeftToAblate = false;
					foreach (AblationGroup ag in ablationSwitchGroups)
					{
						if (ag.active && (ag.countUsage < ag.countLimit))
						{
							groupsLeftToAblate = true;
							ag.countUsage += 1;
							activeAblationGroupQueue.Enqueue(ag);
						}
						else if (ag.countUsage == ag.countLimit)
						{
							ag.isComplete = true;
							ag.active = false;
							ag.activeDuration = 0;
						}
					}
					if (!groupsLeftToAblate)
						imcState = imcStatusEnum.isComplete;
					else
						imcState = imcStatusEnum.inAblation;
					collectionTimer.Change(0, System.Threading.Timeout.Infinite);
					break;
				case imcStatusEnum.isStopped:
					dataWriteFile.Close();
					collectionTimer.Dispose();
					break;
				case imcStatusEnum.isComplete:
					dataWriteFile.Close();
					collectionTimer.Dispose();
					break;
			}
		}


		private void PopulateOppositeElectrodes()
		{
			foreach (KeyValuePair<string, Tuple<string, List<string>, List<string>>> entry in __electrodeGroups)
			{
				if (entry.Key.Length < 2)
				{
					foreach (string oppCode in entry.Value.Item1.Split('.'))
					{
						entry.Value.Item3.AddRange(__electrodeGroups[oppCode].Item2);
					}
				}
			}
		}


		public string GroupsAblating()
		{
			List<string> returnStringList = new List<string>();
			foreach (AblationGroup ag in ablationSwitchGroups)
			{
				if (ag.isComplete)
					returnStringList.Insert(0, String.Join(".", ag.activeSides));
				else if (ag.active)
					returnStringList.Add(String.Join(".", ag.activeSides));
			}
			return String.Join(",", returnStringList);
		}


		public string SamplesTaken()
		{
			List<string> returnStringList = new List<string>();
			foreach (AblationGroup ag in ablationSwitchGroups)
			{
				if (ag.isComplete)
					returnStringList.Insert(0, ag.countUsage.ToString());
				else if (ag.active)
					returnStringList.Add(ag.countUsage.ToString());
			}
			return String.Join(",", returnStringList);
		}


		private void WritePermutationImpedanceToFile(Dictionary<string, List<string>> permutation, string impedance, string phase)
		{
			string currentDate = DateTime.Now.ToString("yyyy-MM-dd");
			string currentTime = (DateTime.Now - __recordingStartTime).TotalSeconds.ToString(); //DateTime.Now.ToString("hh:mm:ss.fff");
			string posCode = String.Join(".", permutation["PositiveCode"]); // TODO: this might not be right
			//string posCode = permutation["PositiveCode"][0];
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

			OutputQueue.Enqueue(dataRow);

			lock (_syncLock)
			{
				__datatableImpedance.Rows.Add(dataRow);
			}
			mainRef.addLineToImpedanceBox(__datatableImpedance);
		}

	}
}

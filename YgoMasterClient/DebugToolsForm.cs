#if DEBUG
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace YgoMasterClient
{
    /// <summary>
    /// Simple GUI front-end for the console commands in <see cref="ConsoleHelper"/>.
    /// This is the debug/dev tools panel, opened automatically in DEBUG builds.
    /// </summary>
    static class DebugToolsGui
    {
        static readonly object syncObj = new object();
        static DebugToolsForm form;
        static Thread uiThread;

        public static void Show()
        {
            EnsureUiThread();
            if (form == null || form.IsDisposed)
            {
                return;
            }
            form.BeginInvoke((Action)delegate
            {
                if (!form.Visible)
                {
                    form.Show();
                }
                form.Activate();
            });
        }

        static void EnsureUiThread()
        {
            lock (syncObj)
            {
                if (uiThread != null && uiThread.IsAlive && form != null && !form.IsDisposed)
                {
                    return;
                }

                AutoResetEvent formReady = new AutoResetEvent(false);
                uiThread = new Thread(delegate ()
                {
                    form = new DebugToolsForm();
                    IntPtr unused = form.Handle;// Ensure the window handle exists before cross-thread BeginInvoke calls
                    Console.SetOut(new FormOutputTextWriter(form));
                    formReady.Set();
                    Application.Run(form);
                });
                uiThread.Name = "YgoMasterDebugTools";
                uiThread.IsBackground = true;
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();
                formReady.WaitOne();
            }
        }
    }

    class FormOutputTextWriter : TextWriter
    {
        DebugToolsForm form;

        public FormOutputTextWriter(DebugToolsForm form)
        {
            this.form = form;
        }

        public override Encoding Encoding { get { return Encoding.UTF8; } }

        public override void Write(string value)
        {
            form.AppendOutput(value, true);
        }

        public override void WriteLine(string value)
        {
            form.AppendOutput(value + Environment.NewLine, true);
        }
    }

    class DebugToolsForm : Form
    {
        TextBox commandInput;
        TextBox output;
        ListBox commandList;

        // Full list of currently implemented top-level console commands from ConsoleHelper.
        // Commands that require arguments include a sample payload that can be edited before running.
        static readonly Tuple<string, string>[] presetCommands = new Tuple<string, string>[]
        {
            Tuple.Create("classes - dump Assembly-CSharp class list", "classes"),
            Tuple.Create("methods - dump methods on class (sample)", "methods TitleViewController"),
            Tuple.Create("hierarchy - dump active UI hierarchy JSON", "hierarchy"),
            Tuple.Create("dumpassets - dump tracked raw asset paths", "dumpassets"),
            Tuple.Create("itemid - build IDS_ITEM value dumps", "itemid"),
            Tuple.Create("itemid_old - legacy IDS_ITEM dump flow", "itemid_old"),
            Tuple.Create("itemid_enum - generate IDS_ITEM enum text", "itemid_enum"),
            Tuple.Create("packnames - list card pack names", "packnames"),
            Tuple.Create("packimages - discover/write pack image dump", "packimages"),
            Tuple.Create("text - read one IDS value (sample)", "text IDS_CARD.STYLE3"),
            Tuple.Create("textenum - dump one IDS enum (sample)", "textenum IDS_CARD"),
            Tuple.Create("textdump - dump all IDS enums", "textdump"),
            Tuple.Create("textreload - reload custom IDS data", "textreload"),
            Tuple.Create("soloreload - reload custom solo data", "soloreload"),
            Tuple.Create("resultcodes - extract network result-code enums", "resultcodes"),
            Tuple.Create("locate - resolve /LocalData/ asset path (sample)", "locate Card/Images/Illust/tcg/46986414"),
            Tuple.Create("locateraw - resolve path without auto-convert (sample)", "locateraw Card/Images/Illust/tcg/46986414"),
            Tuple.Create("crc - print assetbundle disk path, select file if found (sample)", "crc Card/Images/Illust/tcg/46986414"),
            Tuple.Create("carddata_path - print card-data IntIdPath", "carddata_path"),
            Tuple.Create("carddata - dump internal card-data files", "carddata"),
            Tuple.Create("updatediff - dump refs/helpers for client update diffing", "updatediff"),
            Tuple.Create("updatejson - update ClientWork by path/value (sample)", "updatejson $.Master.DebugFlag 1"),
            Tuple.Create("updatejsonraw - update ClientWork with raw JSON (sample)", "updatejsonraw {\"Master\":{\"DebugFlag\":1}}"),
            Tuple.Create("logjson - print ClientWork JSON at path (sample)", "logjson $.Master"),
            Tuple.Create("cardswithart - list art cards missing in CardRare", "cardswithart"),
            Tuple.Create("dumphome - dump last-seen home UI hierarchy", "dumphome"),
            Tuple.Create("vcargs - print top view-controller args", "vcargs"),
            Tuple.Create("pvpops - generate PvpOperation enum names", "pvpops"),
            Tuple.Create("unityplayerupdate - refresh UnityPlayer addresses from PDB", "unityplayerupdate"),
            Tuple.Create("bgreload - reload custom background assets", "bgreload"),
            Tuple.Create("gacha_get_probability - call gacha probability API (sample)", "gacha_get_probability 1 1"),
            Tuple.Create("solo_clear - clear all solo progression data", "solo_clear"),
            Tuple.Create("dismantle_all_cards - mass dismantle by rarity (sample)", "dismantle_all_cards UltraRare"),
            Tuple.Create("num_secrets - log unlocked secret-pack count", "num_secrets"),
            Tuple.Create("craft_secrets - craft unlock cards for secret packs", "craft_secrets"),
            Tuple.Create("auto_free_pull - open all available free pulls", "auto_free_pull"),
            Tuple.Create("card_base_data_size - print CardBaseData struct size", "card_base_data_size"),
            Tuple.Create("pop - internal debug pop helper", "pop"),
        };

        public DebugToolsForm()
        {
            Text = "YgoMaster Debug Tools";
            Width = 700;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(620, 440);
            TopMost = true;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(32, 34, 40);
            ForeColor = Color.FromArgb(230, 231, 233);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label quickToolsLabel = new Label();
            quickToolsLabel.Text = "Quick Tools";
            quickToolsLabel.Dock = DockStyle.Fill;
            quickToolsLabel.TextAlign = ContentAlignment.BottomLeft;
            quickToolsLabel.ForeColor = Color.FromArgb(157, 197, 255);

            commandList = new ListBox();
            commandList.Dock = DockStyle.Fill;
            commandList.BorderStyle = BorderStyle.FixedSingle;
            commandList.BackColor = Color.FromArgb(24, 26, 31);
            commandList.ForeColor = ForeColor;
            commandList.IntegralHeight = false;
            foreach (Tuple<string, string> cmd in presetCommands)
            {
                commandList.Items.Add(cmd.Item1);
            }
            commandList.DoubleClick += delegate
            {
                RunSelectedPreset();
            };

            Button runSelectedButton = new Button();
            runSelectedButton.Text = "Run Selected Command";
            runSelectedButton.Dock = DockStyle.Fill;
            StyleButton(runSelectedButton);
            runSelectedButton.Click += delegate
            {
                RunSelectedPreset();
            };

            Label customCommandLabel = new Label();
            customCommandLabel.Text = "Custom Command";
            customCommandLabel.Dock = DockStyle.Fill;
            customCommandLabel.TextAlign = ContentAlignment.BottomLeft;
            customCommandLabel.ForeColor = Color.FromArgb(157, 197, 255);

            TableLayoutPanel inputLayout = new TableLayoutPanel();
            inputLayout.Dock = DockStyle.Fill;
            inputLayout.ColumnCount = 2;
            inputLayout.RowCount = 1;
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

            commandInput = new TextBox();
            commandInput.Dock = DockStyle.Fill;
            commandInput.BorderStyle = BorderStyle.FixedSingle;
            commandInput.BackColor = Color.FromArgb(24, 26, 31);
            commandInput.ForeColor = ForeColor;
            commandInput.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    RunCommand(commandInput.Text);
                    e.SuppressKeyPress = true;
                }
            };

            Button runButton = new Button();
            runButton.Text = "Run";
            runButton.Dock = DockStyle.Fill;
            StyleButton(runButton);
            runButton.Click += delegate
            {
                RunCommand(commandInput.Text);
            };

            inputLayout.Controls.Add(commandInput, 0, 0);
            inputLayout.Controls.Add(runButton, 1, 0);

            Label outputLabel = new Label();
            outputLabel.Text = "Output";
            outputLabel.Dock = DockStyle.Fill;
            outputLabel.TextAlign = ContentAlignment.BottomLeft;
            outputLabel.ForeColor = Color.FromArgb(157, 197, 255);

            output = new TextBox();
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Dock = DockStyle.Fill;
            output.BorderStyle = BorderStyle.FixedSingle;
            output.BackColor = Color.FromArgb(20, 21, 24);
            output.ForeColor = Color.FromArgb(203, 237, 195);
            output.Font = new Font("Consolas", 9f);

            layout.Controls.Add(quickToolsLabel, 0, 0);
            layout.Controls.Add(commandList, 0, 1);
            layout.Controls.Add(runSelectedButton, 0, 2);
            layout.Controls.Add(customCommandLabel, 0, 3);
            layout.Controls.Add(inputLayout, 0, 4);
            layout.Controls.Add(outputLabel, 0, 5);
            layout.Controls.Add(output, 0, 6);

            Controls.Add(layout);
        }

        static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(65, 105, 180);
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            button.Margin = new Padding(0, 3, 0, 3);
        }

        void RunSelectedPreset()
        {
            int index = commandList.SelectedIndex;
            if (index >= 0 && index < presetCommands.Length)
            {
                RunCommand(presetCommands[index].Item2);
            }
        }

        void RunCommand(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return;
            }
            AppendOutput("> " + commandLine);
            Win32Hooks.Invoke(delegate
            {
                try
                {
                    ConsoleHelper.ExecuteCommand(commandLine);
                }
                catch (Exception ex)
                {
                    AppendOutput(ex.ToString());
                }
            });
        }

        void AppendOutput(string text)
        {
            AppendOutput(text + Environment.NewLine, true);
        }

        public void AppendOutput(string text, bool isRawWrite)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)(() => output.AppendText(text)));
                }
                catch
                {
                }
                return;
            }
            output.AppendText(text);
        }
    }
}
#endif

// RyGT loader: the steering wheel and pedal mapping window.
// It edits ryty-input.ini (the wheel lines: JOYAXIS / JOYBUTTON / JOYHAT) and ryty-wheel.ini,
// which libScePad reads, and keeps every other line of both files as it is. Live wheel state
// comes from tools\ryty-wheel-probe.exe, which reads joysticks the same way libScePad does, so
// the axis and button numbers here are the ones the game uses.
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RyGT
{
    class WheelForm : Form
    {
        /// One joystick as the probe reports it, with its latest state.
        class Device
        {
            public int Instance;
            public string Name;
            public bool Wheel;
            public int[] Axes = new int[0];
            public HashSet<int> Buttons = new HashSet<int>();
            public int[] Hats = new int[0];
        }

        /// One mappable control: an analog action (JOYAXIS) or a pad button (JOYBUTTON / JOYHAT).
        class Row
        {
            public string Action, Label;
            public bool Analog;
            public string Source;        // "JOYAXIS:1", "JOYBUTTON:13", "JOYHAT:UP" or null
            public string DeviceName;    // null: the wheel
            public string OwnedLine;     // the ini line loaded into this row; null: no line
            public bool Invert;
            public DecoLabel Name;
            public DecoLabel Value;
            public DarkBar Bar;
            public Control InvertCell;   // always-visible cell holder; the invisible checkbox does not take a table cell
            public DarkCheckBox InvertBox;
            public Button Detect;
            public FlowLayoutPanel Flow;
        }

        static readonly Row[] Rows =
        {
            new Row { Action = "Steering", Label = "Steering", Analog = true },
            new Row { Action = "Throttle", Label = "Throttle pedal", Analog = true },
            new Row { Action = "Brake", Label = "Brake pedal", Analog = true },
            new Row { Action = "Clutch", Label = "Clutch pedal", Analog = true },
            new Row { Action = "R1", Label = "Shift up (right paddle, R1)" },
            new Row { Action = "L1", Label = "Shift down (left paddle, L1)" },
            new Row { Action = "Cross", Label = "Cross (confirm)" },
            new Row { Action = "Circle", Label = "Circle (back)" },
            new Row { Action = "Square", Label = "Square" },
            new Row { Action = "Triangle", Label = "Triangle" },
            new Row { Action = "Options", Label = "Options (pause)" },
            new Row { Action = "Up", Label = "D-pad up" },
            new Row { Action = "Down", Label = "D-pad down" },
            new Row { Action = "Left", Label = "D-pad left" },
            new Row { Action = "Right", Label = "D-pad right" },
            new Row { Action = "L2", Label = "L2" },
            new Row { Action = "R2", Label = "R2" },
            new Row { Action = "L3", Label = "L3" },
            new Row { Action = "R3", Label = "R3" },
            new Row { Action = "RightStickUp", Label = "Right stick up" },
            new Row { Action = "RightStickRight", Label = "Right stick right" },
            new Row { Action = "RightStickDown", Label = "Right stick down" },
            new Row { Action = "RightStickLeft", Label = "Right stick left" },
            new Row { Action = "TouchLeft", Label = "Touchpad (left)" },
        };

        readonly string inputPath, wheelPath, probePath;
        readonly TableLayoutPanel grid;
        readonly Dictionary<int, Device> devices = new Dictionary<int, Device>();
        int hoverRow = -1;
        readonly List<int> deviceOrder = new List<int>();
        readonly object gate = new object();
        readonly Label status, deviceList, rawInput;
        readonly NumericUpDown rotation, gain, damping, detail;
        readonly CheckBox invertForces, gameWheelMode;
        readonly ComboBox presentAs;
        // The wheel models the game can be told it has (ryty-wheel.ini "Present"); the game
        // uses that model's own force feedback driver.
        static readonly string[] PresentNames = { "Fanatec direct drive (recommended)", "Logitech G29" };
        static readonly string[] PresentIds = { "0eb7:0e05", "046d:c24f" };
        readonly Timer timer;
        Process probe;

        // Detection: the row being detected, the state when it started, and when it gives up.
        Row detecting;
        Dictionary<int, int[]> baseAxes;
        Dictionary<int, HashSet<int>> baseButtons;
        Dictionary<int, int[]> baseHats;
        DateTime detectUntil;

        public WheelForm(string root)
        {
            inputPath = Path.Combine(root, "ryty-input.ini");
            wheelPath = Path.Combine(root, "ryty-wheel.ini");
            probePath = Path.Combine(root, "tools", "ryty-wheel-probe.exe");
            if (!File.Exists(probePath)) probePath = Path.Combine(root, "ryty-wheel-probe.exe");

            Text = "ryGT - Steering wheel";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(800, 840);
            MinimumSize = new Size(640, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 14, 18, 0) };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(outer);
            outer.Controls.Add(new Label { Text = "Steering wheel", AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Margin = new Padding(0, 0, 0, 4) });

            outer.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 6),
                ForeColor = Dark.DimText, Tag = "heading",
                Text = "Map your racing wheel and pedals. Press Detect, then turn the wheel, press the pedal or press the " +
                       "button you want for that control. The game sees the wheel as the model picked below."
            });
            deviceList = new Label { AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Dark.DimText, Margin = new Padding(0, 0, 0, 6) };
            outer.Controls.Add(deviceList);
            // What the window receives right now, so it is easy to see that the wheel is being read.
            rawInput = new Label { AutoSize = true, MaximumSize = new Size(740, 0), ForeColor = Dark.DimText, Margin = new Padding(0, 0, 0, 6), Font = new Font("Consolas", 8.5f) };
            outer.Controls.Add(rawInput);

            var table = new DecoratedTable { ColumnCount = 5 };
            table.AutoSize = true;
            table.Dock = DockStyle.Top;
            table.Margin = new Padding(0);
            grid = table;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
            foreach (var heading in new[] { "Control", "Mapped to", "Live", "", "" })
            {
                table.Controls.Add(new Label { Text = heading.ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Dark.Heading, Tag = "heading", Margin = new Padding(0, 0, 0, 4) });
            }
            foreach (var row in Rows)
            {
                var r = row;
                int k = Array.IndexOf(Rows, row);
                var name = new DecoLabel { Text = r.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
                name.Decorate = RowDecorate(name, k, false);
                r.Name = name;
                table.Controls.Add(name);
                r.Value = new DecoLabel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
                r.Value.Decorate = RowDecorate(r.Value, k, false);
                table.Controls.Add(r.Value);
                r.Bar = new DarkBar { Minimum = 0, Maximum = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 10, Margin = new Padding(0, 6, 12, 6) };
                table.Controls.Add(r.Bar);
                // The cell holder is always visible, so the table keeps its cell assignment; the
                // checkbox inside it can come and go. Margins center the 22px checkbox in the 44px row.
                var invertCell = new DecoPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Width = 66, Height = 44 };
                invertCell.Decorate = RowDecorate(invertCell, k, true);
                r.InvertCell = invertCell;
                if (r.Analog)
                {
                    r.InvertBox = new DarkCheckBox { Text = "Invert", AutoSize = true };
                    r.InvertBox.Top = 11;   // centers the 22px box in the 44px row
                    r.InvertBox.Background = RowDecorate(r.InvertBox, k, false);
                    r.InvertBox.CheckedChanged += delegate { r.Invert = r.InvertBox.Checked; };
                    r.InvertCell.Controls.Add(r.InvertBox);
                }
                table.Controls.Add(r.InvertCell);
                var buttons = new DecoFlow { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                buttons.Decorate = RowDecorate(buttons, k, true);
                r.Flow = buttons;
                r.Detect = new Button { Text = "Detect", AutoSize = true };
                r.Detect.Click += delegate { StartDetect(r); };
                var clear = new Button { Text = "Clear", AutoSize = true };
                clear.Click += delegate { r.Source = null; r.DeviceName = null; r.Invert = false; Show(r); };
                buttons.Controls.Add(r.Detect);
                buttons.Controls.Add(clear);
                table.Controls.Add(buttons);
            }
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), Padding = new Padding(0, 0, 12, 0) };
            scroll.Controls.Add(table);
            var mapping = new Card(Dark.Panel) { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 6, 10), Margin = new Padding(0, 6, 0, 10) };
            mapping.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mapping.Controls.Add(scroll);
            outer.Controls.Add(mapping);

            // Faint row separators, a theme border where a control group begins, and a hover band
            // on the row under the mouse. Painted as the table's background so the transparent
            // row controls show it through instead of covering it.
            table.Decorate = delegate (Graphics g)
            {
                if (hoverRow >= 0)
                {
                    int top = Rows[hoverRow].Flow.Top;
                    int rowBottom = hoverRow + 1 < Rows.Length ? Rows[hoverRow + 1].Flow.Top : top + Rows[hoverRow].Flow.Height;
                    using (var band = new SolidBrush(Dark.Hover)) g.FillRectangle(band, 0, top, table.Width, rowBottom - top);
                }
                using (var faint = new Pen(Color.FromArgb(34, 34, 34), 1))
                using (var border = new Pen(Dark.Border, 1))
                    for (int k = 0; k < Rows.Length; ++k)
                    {
                        bool group = k == 0 || k == 1 || k == 4 || k == 6 || k == 11 || k == 15 || k == 19 || k == 23;
                        g.DrawLine(group ? border : faint, 0, Rows[k].Flow.Top, table.Width, Rows[k].Flow.Top);
                    }
            };
            Application.AddMessageFilter(new HoverFilter(this, table));

            var settings = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            settings.Controls.Add(new Label { Text = "Wheel rotation (degrees, as set in your wheel's software)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            rotation = new NumericUpDown { Minimum = 90, Maximum = 2880, Increment = 10, Value = 900, Width = 80 };
            settings.Controls.Add(rotation);
            settings.Controls.Add(new Label { Text = "Force feedback strength (%)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            gain = new NumericUpDown { Minimum = 0, Maximum = 200, Increment = 10, Value = 100, Width = 80 };
            settings.Controls.Add(gain);
            settings.Controls.Add(new Label { Text = "Damping (%, steadies the wheel on straights)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            damping = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 5, Value = 10, Width = 80 };
            settings.Controls.Add(damping);
            settings.Controls.Add(new Label { Text = "Kerb and road detail (%, extra force for bumps)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            detail = new NumericUpDown { Minimum = 0, Maximum = 200, Increment = 10, Value = 0, Width = 80 };
            settings.Controls.Add(detail);
            invertForces = new DarkCheckBox { Text = "Reverse force feedback direction", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            settings.Controls.Add(invertForces);
            settings.Controls.Add(new Label { AutoSize = true });
            gameWheelMode = new DarkCheckBox { Text = "Pad reports as the wheel (experimental, leave off: the game then ignores the wheel's forces and input)", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            settings.Controls.Add(gameWheelMode);
            settings.SetColumnSpan(gameWheelMode, 2);
            settings.Controls.Add(new Label { Text = "Game sees the wheel as (its force feedback driver)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            presentAs = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, FlatStyle = FlatStyle.Flat };
            presentAs.Items.AddRange(PresentNames);
            presentAs.SelectedIndex = 0;
            settings.Controls.Add(presentAs);
            var feel = new Card(Dark.Panel) { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 12), Margin = new Padding(0, 0, 0, 4) };
            feel.Controls.Add(new Label { Text = "Force feedback and wheel", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, 4) });
            feel.Controls.Add(settings);
            outer.Controls.Add(feel);

            // Status and Save/Cancel stay pinned to the bottom, below the scrolling list.
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18, 0, 18, 14) };
            status = new Label { AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Dark.DimText, Margin = new Padding(0, 4, 0, 4) };
            bottom.Controls.Add(status);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true, Tag = "accent" };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += delegate
            {
                try { Save(); DialogResult = DialogResult.OK; Close(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the wheel settings: " + e.Message); }
            };
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            bottom.Controls.Add(actions);
            Controls.Add(bottom);
            outer.BringToFront();
            CancelButton = cancel;
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            foreach (var row in Rows) { row.Source = null; row.DeviceName = null; row.Invert = false; row.OwnedLine = null; row.InvertBox = null; row.InvertCell.Controls.Clear(); }
            LoadSettings();
            foreach (var row in Rows) Show(row);
            Dark.Apply(this);
            HandleCreated += delegate { Dark.TitleBar(this); };

            timer = new Timer { Interval = 50 };
            timer.Tick += delegate { Tick(); };
            Shown += delegate { StartProbe(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); StopProbe(); };
        }

        // ---------------------------------------------------------------- probe

        void StartProbe()
        {
            if (!File.Exists(probePath))
            {
                status.Text = "tools\\ryty-wheel-probe.exe is missing, so the wheel cannot be detected here. " +
                              "You can still edit ryty-input.ini by hand.";
                return;
            }
            try
            {
                var info = new ProcessStartInfo(probePath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture))
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                };
                probe = new Process { StartInfo = info };
                probe.OutputDataReceived += (sender, e) => { if (e.Data != null) OnProbeLine(e.Data); };
                probe.Start();
                probe.BeginOutputReadLine();
                status.Text = "Looking for wheels...";
            }
            catch (Exception e)
            {
                status.Text = "Could not start the wheel probe: " + e.Message;
            }
        }

        void StopProbe()
        {
            try { if (probe != null && !probe.HasExited) probe.Kill(); } catch { }
        }

        void OnProbeLine(string line)
        {
            var parts = line.Split(new[] { ' ' }, 9);
            lock (gate)
            {
                if (parts[0] == "devices")
                {
                    devices.Clear();
                    deviceOrder.Clear();
                }
                else if (parts[0] == "device" && parts.Length >= 9)
                {
                    var device = new Device
                    {
                        Instance = int.Parse(parts[1], CultureInfo.InvariantCulture),
                        Wheel = parts[4] == "1",
                        Name = parts[8]
                    };
                    devices[device.Instance] = device;
                    deviceOrder.Add(device.Instance);
                }
                else if (parts[0] == "state" && parts.Length >= 5)
                {
                    Device device;
                    if (!devices.TryGetValue(int.Parse(parts[1], CultureInfo.InvariantCulture), out device)) return;
                    device.Axes = parts[2] == "-" ? new int[0] : parts[2].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    device.Buttons = parts[3] == "-" ? new HashSet<int>() : new HashSet<int>(parts[3].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)));
                    device.Hats = parts[4] == "-" ? new int[0] : parts[4].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                }
            }
        }

        /// The device libScePad uses when a line names none: the first wheel, else the first device with axes.
        Device Primary()
        {
            foreach (var id in deviceOrder) if (devices[id].Wheel) return devices[id];
            foreach (var id in deviceOrder) if (devices[id].Axes.Length > 0) return devices[id];
            return null;
        }

        Device Resolve(Row row)
        {
            if (row.DeviceName == null) return Primary();
            var wanted = row.DeviceName.ToLowerInvariant();
            foreach (var id in deviceOrder) if (devices[id].Name.ToLowerInvariant().Contains(wanted)) return devices[id];
            return null;
        }

        // ---------------------------------------------------------------- live view and detection

        void Tick()
        {
            lock (gate)
            {
                deviceList.Text = deviceOrder.Count == 0
                    ? "No wheel or joystick found. Plug it in (and start its software, like Moza Pit House, G HUB or the Thrustmaster drivers)."
                    : "Found: " + string.Join(", ", deviceOrder.Select(id => devices[id].Name + (devices[id].Wheel ? " (wheel)" : "")));
                var lines = deviceOrder.Select(id =>
                {
                    var d = devices[id];
                    return "Live: axes " + string.Join(" ", d.Axes.Select((v, i) => i + ":" + (v * 100 / 32768).ToString(CultureInfo.InvariantCulture) + "%")) +
                           "   buttons " + (d.Buttons.Count == 0 ? "none" : string.Join(",", d.Buttons.OrderBy(b => b))) +
                           (d.Hats.Length > 0 && d.Hats[0] != 0 ? "   hat " + d.Hats[0] : "");
                });
                rawInput.Text = string.Join(Environment.NewLine, lines);
                foreach (var row in Rows) ShowLive(row);
                if (detecting != null) StepDetect();
            }
        }

        void ShowLive(Row row)
        {
            int value = 0;
            var device = row.Source != null ? Resolve(row) : null;
            if (device != null)
            {
                var parts = row.Source.Split(':');
                int index;
                if (parts[0] == "JOYAXIS" && int.TryParse(parts[1], out index) && index < device.Axes.Length)
                {
                    value = (device.Axes[index] + 32768) * 1000 / 65535;
                    if (row.Invert) value = 1000 - value;
                }
                else if (parts[0] == "JOYBUTTON" && int.TryParse(parts[1], out index))
                {
                    value = device.Buttons.Contains(index) ? 1000 : 0;
                }
                else if (parts[0] == "JOYHAT" && device.Hats.Length > 0)
                {
                    value = (device.Hats[0] & HatBit(parts[1])) != 0 ? 1000 : 0;
                }
            }
            row.Bar.Value = Math.Max(0, Math.Min(1000, value));
        }

        void StartDetect(Row row)
        {
            lock (gate)
            {
                if (deviceOrder.Count == 0)
                {
                    status.Text = "No wheel found yet.";
                    return;
                }
                if (detecting != null) detecting.Detect.Text = "Detect";
                detecting = row;
                baseAxes = deviceOrder.ToDictionary(id => id, id => (int[])devices[id].Axes.Clone());
                baseButtons = deviceOrder.ToDictionary(id => id, id => new HashSet<int>(devices[id].Buttons));
                baseHats = deviceOrder.ToDictionary(id => id, id => (int[])devices[id].Hats.Clone());
                detectUntil = DateTime.Now.AddSeconds(10);
                status.Text = row.Analog
                    ? "Now " + (row.Action == "Steering" ? "turn the wheel well to the RIGHT" : "press the " + row.Label.ToLowerInvariant() + " all the way") + "..."
                    : "Now press the button, move the stick or press the hat for " + row.Label + "...";
            }
        }

        void StepDetect()
        {
            int left = (int)Math.Ceiling((detectUntil - DateTime.Now).TotalSeconds);
            if (left > 0 && !status.Text.EndsWith(")")) status.Text += " (" + left + ")";
            else if (left > 0) status.Text = status.Text.Substring(0, status.Text.LastIndexOf(" (")) + " (" + left + ")";
            if (left > 0) detecting.Detect.Text = "Waiting " + left + "s";
            if (DateTime.Now > detectUntil)
            {
                status.Text = "Nothing detected for " + detecting.Label + ".";
                detecting.Detect.Text = "Detect";
                detecting = null;
                return;
            }
            foreach (var id in deviceOrder)
            {
                var device = devices[id];
                int[] start;
                if (!baseAxes.TryGetValue(id, out start)) continue;
                if (detecting.Analog)
                {
                    for (int axis = 0; axis < Math.Min(start.Length, device.Axes.Length); ++axis)
                    {
                        int moved = device.Axes[axis] - start[axis];
                        if (Math.Abs(moved) < 20000) continue;
                        Assign(device, "JOYAXIS:" + axis);
                        // Steering was turned right: if that lowered the axis, the game needs it reversed.
                        if (detecting == null && Rows[0].Source == "JOYAXIS:" + axis && moved < 0 && lastAssigned == Rows[0]) { Rows[0].Invert = true; Show(Rows[0]); }
                        return;
                    }
                }
                else
                {
                    foreach (var button in device.Buttons)
                    {
                        if (baseButtons[id].Contains(button)) continue;
                        Assign(device, "JOYBUTTON:" + button);
                        return;
                    }
                    for (int axis = 0; axis < Math.Min(start.Length, device.Axes.Length); ++axis)
                    {
                        if (device == Primary() && axis == 0) continue;   // the steering wheel axis
                        if (Math.Abs(start[axis]) > 30000) continue;       // a pedal: rests at a range end
                        int moved = device.Axes[axis] - start[axis];
                        if (Math.Abs(moved) < 20000) continue;
                        var axisRow = detecting;
                        Assign(device, "JOYAXIS:" + axis);
                        axisRow.Invert = moved < 0;
                        Show(axisRow);
                        return;
                    }
                    var hats = baseHats[id];
                    for (int hat = 0; hat < Math.Min(1, device.Hats.Length); ++hat)
                    {
                        int pressed = device.Hats[hat] & ~(hat < hats.Length ? hats[hat] : 0);
                        foreach (var direction in new[] { "UP", "RIGHT", "DOWN", "LEFT" })
                        {
                            if ((pressed & HatBit(direction)) == 0) continue;
                            Assign(device, "JOYHAT:" + direction);
                            return;
                        }
                    }
                }
            }
        }

        Row lastAssigned;

        void Assign(Device device, string source)
        {
            var row = detecting;
            lastAssigned = row;
            detecting = null;
            row.Detect.Text = "Detect";
            row.Source = source;
            var primary = Primary();
            row.DeviceName = primary != null && primary.Instance == device.Instance ? null : device.Name;
            row.Invert = false;
            Show(row);
            status.Text = row.Label + ": " + Describe(row) + ".";
        }

        static int HatBit(string direction)
        {
            switch (direction.ToUpperInvariant())
            {
                case "UP": return 1;
                case "RIGHT": return 2;
                case "DOWN": return 4;
                case "LEFT": return 8;
            }
            return 0;
        }

        static string Describe(Row row)
        {
            if (row.Source == null) return "(not mapped)";
            var parts = row.Source.Split(':');
            string text;
            if (parts[0] == "JOYAXIS") text = "axis " + parts[1];
            else if (parts[0] == "JOYBUTTON") text = "button " + parts[1];
            else text = "hat " + parts[1].ToLowerInvariant();
            if (row.Invert && row.Source.StartsWith("JOYAXIS")) text += " (inverted)";
            if (row.DeviceName != null) text += " on " + row.DeviceName;
            return text;
        }

        void Show(Row row)
        {
            row.Value.Text = Describe(row);
            // The checkbox only shows in rows that can carry an INVERT: the analog rows and any
            // row bound to a JOYAXIS source.
            bool wantBox = row.Analog || (row.Source != null && row.Source.StartsWith("JOYAXIS"));
            if (wantBox && row.InvertBox == null)
            {
                row.InvertBox = new DarkCheckBox { Text = "Invert", AutoSize = true, Top = 11 };
                row.InvertBox.Background = RowDecorate(row.InvertBox, Array.IndexOf(Rows, row), false);
                row.InvertBox.CheckedChanged += delegate { row.Invert = row.InvertBox.Checked; };
                row.InvertBox.Checked = row.Invert;
                row.InvertCell.Controls.Add(row.InvertBox);
            }
            else if (!wantBox && row.InvertBox != null)
            {
                row.InvertCell.Controls.Clear();
                row.InvertBox = null;
            }
            if (row.InvertBox != null) row.InvertBox.Checked = row.Invert;
        }

        public void SetHover(int k)
        {
            if (k == hoverRow) return;
            int old = hoverRow;
            hoverRow = k;
            grid.Invalidate();
            InvalidateRow(old);
            InvalidateRow(k);
        }

        // A parent's invalidation does not reach its children; the band lives in theirs too.
        void InvalidateRow(int k)
        {
            if (k < 0 || k >= Rows.Length) return;
            var r = Rows[k];
            r.Name.Invalidate();
            r.Value.Invalidate();
            r.Bar.Invalidate();
            r.InvertCell.Invalidate();
            r.Flow.Invalidate();
            if (r.InvertBox != null) r.InvertBox.Invalidate();
        }

        public int RowAt(int y)
        {
            for (int k = Rows.Length - 1; k >= 0; --k)
                if (y >= Rows[k].Flow.Top) return k;
            return -1;
        }

        readonly Pen faintLine = new Pen(Color.FromArgb(34, 34, 34), 1);
        readonly Pen groupLine = new Pen(Dark.Border, 1);

        /// The hover band and (for the full-height cells) the row line, drawn in the control's
        /// own background so the decoration is not covered by the control.
        Action<Graphics> RowDecorate(Control cell, int k, bool line)
        {
            bool group = k == 0 || k == 1 || k == 4 || k == 6 || k == 11 || k == 15 || k == 19 || k == 23;
            var pen = line ? (group ? groupLine : faintLine) : null;
            return delegate (Graphics g)
            {
                if (hoverRow == k)
                    using (var band = new SolidBrush(Dark.Hover)) g.FillRectangle(band, 0, 0, cell.Width, cell.Height);
                if (pen != null) g.DrawLine(pen, 0, 0, cell.Width, 0);
            };
        }

        /// The mapping grid: paints its own background, because the row controls copy a flat
        /// ancestor color and would cover anything the grid painted.
        class DecoratedTable : TableLayoutPanel
        {
            public Action<Graphics> Decorate;

            public DecoratedTable()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.FromArgb(26, 26, 26);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                base.OnPaintBackground(e);
                if (Decorate != null) Decorate(e.Graphics);
            }
        }

        /// Row controls that carry the row's decoration (hover band, row line) in their own
        /// background, so it continues unbroken through them.
        class DecoLabel : Label
        {
            public Action<Graphics> Decorate;
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                base.OnPaintBackground(e);
                if (Decorate != null) Decorate(e.Graphics);
            }
        }

        class DecoPanel : Panel
        {
            public Action<Graphics> Decorate;
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                base.OnPaintBackground(e);
                if (Decorate != null) Decorate(e.Graphics);
            }
        }

        class DecoFlow : FlowLayoutPanel
        {
            public Action<Graphics> Decorate;
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                base.OnPaintBackground(e);
                if (Decorate != null) Decorate(e.Graphics);
            }
        }

        /// Tracks the mouse over the grid and highlights the row under it.
        class HoverFilter : IMessageFilter
        {
            readonly WheelForm form;
            readonly TableLayoutPanel table;
            public HoverFilter(WheelForm form, TableLayoutPanel table) { this.form = form; this.table = table; }
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != 0x0200 || !form.Visible || !form.IsHandleCreated) return false;   // WM_MOUSEMOVE
                var p = table.PointToClient(Control.MousePosition);
                int k = p.X >= 0 && p.X <= table.Width && p.Y >= 0 && p.Y <= table.Height ? form.RowAt(p.Y) : -1;
                form.SetHover(k);
                return false;
            }
        }

        // ---------------------------------------------------------------- files

        static string ActionOf(string line, out string source)
        {
            source = null;
            var text = line;
            int comment = text.IndexOfAny(new[] { '#', ';' });
            if (comment >= 0) text = text.Substring(0, comment);
            int eq = text.IndexOf('=');
            if (eq <= 0) return null;
            source = text.Substring(eq + 1).Trim();
            return text.Substring(0, eq).Trim();
        }

        static bool IsJoystickSource(string source)
        {
            if (source == null) return false;
            var upper = source.ToUpperInvariant();
            return upper.StartsWith("JOYAXIS:") || upper.StartsWith("JOYBUTTON:") || upper.StartsWith("JOYHAT:");
        }

        void LoadSettings()
        {
            if (File.Exists(inputPath))
            {
                foreach (var line in File.ReadAllLines(inputPath))
                {
                    string source;
                    var action = ActionOf(line, out source);
                    if (action == null || !IsJoystickSource(source)) continue;
                    var row = Rows.FirstOrDefault(r => string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase));
                    if (row == null || row.Source != null) continue;
                    string device = null;
                    int at = source.IndexOf('@');
                    if (at >= 0) { device = source.Substring(at + 1).Trim(); source = source.Substring(0, at).Trim(); }
                    var parts = source.Split(':');
                    row.Source = parts[0].Trim().ToUpperInvariant() + ":" + parts[1].Trim().ToUpperInvariant();
                    row.Invert = parts.Length > 2 && parts[2].Trim().ToUpperInvariant() == "INVERT";
                    row.DeviceName = device;
                    row.OwnedLine = line;
                }
            }
            var settings = ReadWheelSettings();
            int value;
            string text;
            if (settings.TryGetValue("rotation", out text) && int.TryParse(text, out value)) rotation.Value = Math.Max(rotation.Minimum, Math.Min(rotation.Maximum, value));
            if (settings.TryGetValue("ffbgain", out text) && int.TryParse(text, out value)) gain.Value = Math.Max(gain.Minimum, Math.Min(gain.Maximum, value));
            if (settings.TryGetValue("ffbinvert", out text)) invertForces.Checked = text == "1";
            if (settings.TryGetValue("ffbdamping", out text) && int.TryParse(text, out value)) damping.Value = Math.Max(damping.Minimum, Math.Min(damping.Maximum, value));
            if (settings.TryGetValue("ffbdetail", out text) && int.TryParse(text, out value)) detail.Value = Math.Max(detail.Minimum, Math.Min(detail.Maximum, value));
            if (settings.TryGetValue("padiswheel", out text)) gameWheelMode.Checked = text == "1";
            if (settings.TryGetValue("present", out text))
            {
                int index = Array.FindIndex(PresentIds, id => string.Equals(id, text.Trim(), StringComparison.OrdinalIgnoreCase));
                if (index < 0 && text.Trim().StartsWith("0eb7", StringComparison.OrdinalIgnoreCase)) index = 0;
                if (index >= 0) presentAs.SelectedIndex = index;
            }
        }

        Dictionary<string, string> ReadWheelSettings()
        {
            var result = new Dictionary<string, string>();
            if (!File.Exists(wheelPath)) return result;
            foreach (var line in File.ReadAllLines(wheelPath))
            {
                string value;
                var key = ActionOf(line, out value);
                if (key != null) result[key.ToLowerInvariant()] = value;
            }
            return result;
        }

        static string LineOf(Row row)
        {
            var line = row.Action + " = " + row.Source;
            if (row.Invert && row.Source != null && row.Source.StartsWith("JOYAXIS")) line += ":INVERT";
            if (row.DeviceName != null) line += " @ " + row.DeviceName;
            return line;
        }

        void Save()
        {
            // ryty-input.ini: the lines this form loaded are rewritten in place (or removed when
            // cleared); every other line - actions the form does not know and extra bindings for
            // the same action - stays as it is.
            var inputLines = File.Exists(inputPath) ? File.ReadAllLines(inputPath).ToList() : new List<string>();
            var owned = new HashSet<Row>();
            for (int i = 0; i < inputLines.Count; ++i)
            {
                string source;
                var action = ActionOf(inputLines[i], out source);
                var row = action == null ? null : Rows.FirstOrDefault(r => string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase));
                if (row != null && row.OwnedLine != null && !owned.Contains(row) && inputLines[i] == row.OwnedLine)
                {
                    owned.Add(row);
                    if (row.Source == null) inputLines.RemoveAt(i);
                    else inputLines[i] = LineOf(row);
                }
            }
            bool appended = false;
            foreach (var row in Rows)
            {
                if (row.Source == null || owned.Contains(row)) continue;
                if (!appended)
                {
                    inputLines.RemoveAll(l => l.Trim() == "# Racing wheel (written by the RyGT launcher)");
                    while (inputLines.Count > 0 && inputLines[inputLines.Count - 1].Trim().Length == 0) inputLines.RemoveAt(inputLines.Count - 1);
                    if (inputLines.Count > 0) inputLines.Add("");
                    inputLines.Add("# Racing wheel (written by the RyGT launcher)");
                    appended = true;
                }
                inputLines.Add(LineOf(row));
            }
            File.WriteAllLines(inputPath, inputLines, new UTF8Encoding(false));

            // ryty-wheel.ini: the settings edited here are replaced in place; others stay.
            var values = new Dictionary<string, string>
            {
                { "Rotation", ((int)rotation.Value).ToString(CultureInfo.InvariantCulture) },
                { "FfbGain", ((int)gain.Value).ToString(CultureInfo.InvariantCulture) },
                { "FfbInvert", invertForces.Checked ? "1" : "0" },
                { "FfbDamping", ((int)damping.Value).ToString(CultureInfo.InvariantCulture) },
                { "FfbDetail", ((int)detail.Value).ToString(CultureInfo.InvariantCulture) },
                { "PadIsWheel", gameWheelMode.Checked ? "1" : "0" },
                { "Present", PresentIds[Math.Max(0, presentAs.SelectedIndex)] },
            };
            var lines = File.Exists(wheelPath) ? File.ReadAllLines(wheelPath).ToList() : new List<string> { "# Racing wheel settings for RyGT (also editable in the launcher)." };
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; ++i)
            {
                string value;
                var key = ActionOf(lines[i], out value);
                if (key == null) continue;
                var match = values.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                lines[i] = match + " = " + values[match];
                written.Add(match);
            }
            foreach (var pair in values) if (!written.Contains(pair.Key)) lines.Add(pair.Key + " = " + pair.Value);
            File.WriteAllLines(wheelPath, lines, new UTF8Encoding(false));
        }
    }
}

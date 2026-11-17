namespace SpotifyWallpaper;

// "가사 표시" 단축키를 직접 눌러서 정하는 작은 창
internal sealed class HotkeyDialog : Form
{
    readonly Label _combo;
    readonly Button _ok;
    Hotkey? _captured;

    // 확인을 눌렀을 때의 결과 ("" = 사용 안 함)
    public string Result { get; private set; } = "";

    public HotkeyDialog(string current)
    {
        Text = "단축키 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Segoe UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 176);

        var info = new Label
        {
            Text = "가사 표시를 켜고 끌 조합을 누르세요.\nCtrl 또는 Alt를 포함해야 합니다.",
            Location = new Point(18, 14),
            Size = new Size(364, 38),
        };
        _combo = new Label
        {
            Text = Hotkey.TryParse(current, out var cur) ? cur.ToString() : "사용 안 함",
            Font = new Font("Segoe UI Semibold", 15f),
            TextAlign = ContentAlignment.MiddleCenter,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(18, 58),
            Size = new Size(364, 52),
        };
        _ok = new Button { Text = "확인", Location = new Point(126, 128), Size = new Size(82, 30), Enabled = false };
        var off = new Button { Text = "사용 안 함", Location = new Point(214, 128), Size = new Size(82, 30) };
        var cancel = new Button { Text = "취소", Location = new Point(302, 128), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };

        _ok.Click += (_, _) => Finish(_captured?.ToString() ?? "");
        off.Click += (_, _) => Finish("");
        CancelButton = cancel;
        Controls.AddRange(new Control[] { info, _combo, _ok, off, cancel });
        ActiveControl = cancel; // 조합 없이 Enter를 눌러도 설정이 바뀌지 않게
    }

    void Finish(string result)
    {
        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    // 버튼이 키를 먼저 가져가기 전에 조합을 읽는다 (Enter·Space 포함)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) return base.ProcessCmdKey(ref msg, keyData); // 취소
        if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) return base.ProcessCmdKey(ref msg, keyData);

        if (Hotkey.FromKeyData(keyData) is { } hk)
        {
            if (hk.HasRequiredModifier)
            {
                _captured = hk;
                _combo.Text = hk.ToString();
                _ok.Enabled = true;
                return true;
            }
            // 조합 없이 누른 Enter·Space는 평소처럼 버튼을 누르는 데 쓰게 둔다
            if (keyData is Keys.Enter or Keys.Space) return base.ProcessCmdKey(ref msg, keyData);
            _combo.Text = "Ctrl 또는 Alt와 함께 누르세요";
            return true;
        }
        return true; // Ctrl·Alt·Shift만 누른 상태
    }
}

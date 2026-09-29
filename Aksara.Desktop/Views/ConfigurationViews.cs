using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Aksara.Contracts;
using Aksara.Desktop.Services;

namespace Aksara.Desktop.Views;

internal sealed class ProgramConfigurationView : UserControl
{
    private readonly AksaraApiClient _api = AksaraApiClient.Shared;
    private readonly TextBox _company = Box();
    private readonly TextBox _owner = Box();
    private readonly TextBox _address = Box(multiline: true);
    private readonly TextBox _city = Box();
    private readonly TextBox _phone = Box();
    private readonly TextBox _email = Box();
    private readonly TextBox _taxNumber = Box();
    private readonly CheckBox _taxEnabled = Check("Aktifkan PPN pada transaksi baru");
    private readonly TextBox _taxPercent = Box("0");
    private readonly TextBox _detailRows = Box("20");
    private readonly TextBox _printRows = Box("20");
    private readonly TextBox _salePrefix = Box("PJL");
    private readonly TextBox _purchasePrefix = Box("PBM");
    private readonly TextBox _saleReturnPrefix = Box("RJL");
    private readonly TextBox _purchaseReturnPrefix = Box("RPB");
    private readonly TextBox _cashBankPrefix = Box("KB");
    private readonly TextBox _receiptPrefix = Box("BPT");
    private readonly TextBox _paymentPrefix = Box("BUT");
    private readonly TextBox _checkGiroPrefix = Box("CG");
    private readonly CheckBox _showDateTime = Check("Tampilkan tanggal dan jam cetak");
    private readonly CheckBox _showItemCode = Check("Tampilkan kode barang");
    private readonly CheckBox _showPrintedBy = Check("Tampilkan nama pengguna yang mencetak");
    private readonly ComboBox _headerMode = Combo("Tanpa kop", "Profil perusahaan");
    private readonly TextBox _saleTitle = Box();
    private readonly TextBox _purchaseTitle = Box();
    private readonly TextBox _saleReturnTitle = Box();
    private readonly TextBox _purchaseReturnTitle = Box();
    private readonly TextBox _deliveryTitle = Box();
    private readonly TextBox _cashBankTitle = Box();
    private readonly TextBox _signature1 = Box();
    private readonly TextBox _signature2 = Box();
    private readonly TextBox _signature3 = Box();
    private readonly TextBox _footer = Box(multiline: true);
    private readonly Button _save = Button("Simpan", 96);
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
    private ProgramConfigurationDto? _current;

    public event EventHandler? CloseRequested;
    public event EventHandler? Saved;

    public ProgramConfigurationView()
    {
        var root = new DockPanel { Background = Brushes.White };
        var title = Header("Konfigurasi Program");
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var buttons = new DockPanel { Margin = new Thickness(14, 8, 14, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reload = Button("Muat Ulang", 100);
        var close = Button("Tutup", 90);
        reload.Click += async (_, _) => await LoadAsync();
        _save.Click += async (_, _) => await SaveAsync();
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        right.Children.Add(reload); right.Children.Add(_save); right.Children.Add(close);
        buttons.Children.Add(_status); DockPanel.SetDock(right, Dock.Right); buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var tabs = new TabControl { Margin = new Thickness(14, 8, 14, 0) };
        tabs.Items.Add(Tab("Profil Perusahaan", BuildProfile()));
        tabs.Items.Add(Tab("Transaksi", BuildTransactions()));
        tabs.Items.Add(Tab("Penomoran", BuildNumbering()));
        tabs.Items.Add(Tab("Bukti Cetak", BuildPrint()));
        root.Children.Add(tabs);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private FrameworkElement BuildProfile()
    {
        var grid = FormGrid();
        AddRow(grid, "Nama Perusahaan", _company);
        AddRow(grid, "Nama Pemilik", _owner);
        AddRow(grid, "Alamat", _address, 72);
        AddRow(grid, "Kota", _city);
        AddRow(grid, "Telepon", _phone);
        AddRow(grid, "Email", _email);
        AddRow(grid, "NPWP", _taxNumber);
        return Scroll(grid);
    }

    private FrameworkElement BuildTransactions()
    {
        var grid = FormGrid();
        AddRow(grid, "PPN", _taxEnabled);
        AddRow(grid, "PPN Default (%)", _taxPercent);
        AddRow(grid, "Baris Input Transaksi", _detailRows);
        AddRow(grid, "Minimal Baris Cetak", _printRows);
        return Scroll(grid);
    }

    private FrameworkElement BuildNumbering()
    {
        var grid = FormGrid();
        AddRow(grid, "Penjualan", _salePrefix);
        AddRow(grid, "Pembelian", _purchasePrefix);
        AddRow(grid, "Retur Penjualan", _saleReturnPrefix);
        AddRow(grid, "Retur Pembelian", _purchaseReturnPrefix);
        AddRow(grid, "Kas / Bank", _cashBankPrefix);
        AddRow(grid, "Penerimaan Piutang", _receiptPrefix);
        AddRow(grid, "Pembayaran Utang", _paymentPrefix);
        AddRow(grid, "Cek / Giro", _checkGiroPrefix);
        return Scroll(grid);
    }

    private FrameworkElement BuildPrint()
    {
        var grid = FormGrid();
        AddRow(grid, "Kop Bukti Cetak", _headerMode);
        AddRow(grid, "Informasi Cetak", Vertical(_showDateTime, _showItemCode, _showPrintedBy), 76);
        AddRow(grid, "Judul Penjualan", _saleTitle);
        AddRow(grid, "Judul Pembelian", _purchaseTitle);
        AddRow(grid, "Judul Retur Penjualan", _saleReturnTitle);
        AddRow(grid, "Judul Retur Pembelian", _purchaseReturnTitle);
        AddRow(grid, "Judul Surat Jalan", _deliveryTitle);
        AddRow(grid, "Judul Kas / Bank", _cashBankTitle);
        AddRow(grid, "Tanda Tangan 1", _signature1);
        AddRow(grid, "Tanda Tangan 2", _signature2);
        AddRow(grid, "Tanda Tangan 3", _signature3);
        AddRow(grid, "Catatan Bawah", _footer, 68);
        return Scroll(grid);
    }

    private async Task LoadAsync()
    {
        try
        {
            IsEnabled = false;
            _status.Text = "Memuat konfigurasi...";
            _current = await _api.GetProgramConfigurationAsync();
            ProgramConfigurationCache.Set(_current);
            Show(_current);
            _status.Text = $"Terakhir diperbarui {_current.UpdatedAt.LocalDateTime:dd/MM/yyyy HH:mm}";
            _save.IsEnabled = _api.Can("CONFIGURATION", "MANAGE");
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Konfigurasi Program", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsEnabled = true; }
    }

    private async Task SaveAsync()
    {
        if (_current is null) return;
        try
        {
            var request = new SaveProgramConfigurationRequest(
                _company.Text, Empty(_owner), Empty(_address), Empty(_city), Empty(_phone), Empty(_email), Empty(_taxNumber),
                _taxEnabled.IsChecked == true, Decimal(_taxPercent, "PPN Default"), Integer(_detailRows, "Baris Input Transaksi"), Integer(_printRows, "Minimal Baris Cetak"),
                _salePrefix.Text, _purchasePrefix.Text, _saleReturnPrefix.Text, _purchaseReturnPrefix.Text,
                _cashBankPrefix.Text, _receiptPrefix.Text, _paymentPrefix.Text, _checkGiroPrefix.Text,
                _showDateTime.IsChecked == true, _showItemCode.IsChecked == true, _showPrintedBy.IsChecked == true,
                _headerMode.SelectedIndex == 0 ? "NONE" : "TEXT",
                _saleTitle.Text, _purchaseTitle.Text, _saleReturnTitle.Text, _purchaseReturnTitle.Text,
                _deliveryTitle.Text, _cashBankTitle.Text, Empty(_signature1), Empty(_signature2), Empty(_signature3), Empty(_footer),
                _current.Version, _api.CurrentUserName, Environment.MachineName);
            IsEnabled = false;
            _current = await _api.SaveProgramConfigurationAsync(request);
            ProgramConfigurationCache.Set(_current);
            Show(_current);
            _status.Text = "Konfigurasi berhasil disimpan.";
            Saved?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("Konfigurasi program berhasil disimpan dan berlaku untuk transaksi baru.", "Konfigurasi Program", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Konfigurasi Program", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { IsEnabled = true; }
    }

    private void Show(ProgramConfigurationDto value)
    {
        _company.Text=value.CompanyName; _owner.Text=value.OwnerName; _address.Text=value.Address; _city.Text=value.City; _phone.Text=value.Phone; _email.Text=value.Email; _taxNumber.Text=value.TaxNumber;
        _taxEnabled.IsChecked=value.TaxEnabled; _taxPercent.Text=value.DefaultTaxPercent.ToString("0.####", CultureInfo.CurrentCulture); _detailRows.Text=value.TransactionDetailRows.ToString(); _printRows.Text=value.MinimumPrintRows.ToString();
        _salePrefix.Text=value.SaleNumberPrefix; _purchasePrefix.Text=value.PurchaseNumberPrefix; _saleReturnPrefix.Text=value.SaleReturnNumberPrefix; _purchaseReturnPrefix.Text=value.PurchaseReturnNumberPrefix;
        _cashBankPrefix.Text=value.CashBankNumberPrefix; _receiptPrefix.Text=value.CustomerReceiptNumberPrefix; _paymentPrefix.Text=value.SupplierPaymentNumberPrefix; _checkGiroPrefix.Text=value.CheckGiroNumberPrefix;
        _showDateTime.IsChecked=value.ShowPrintDateTime; _showItemCode.IsChecked=value.ShowItemCode; _showPrintedBy.IsChecked=value.ShowPrintedBy;
        _headerMode.SelectedIndex=value.PrintHeaderMode == "NONE" ? 0 : 1;
        _saleTitle.Text=value.SaleTitle; _purchaseTitle.Text=value.PurchaseTitle; _saleReturnTitle.Text=value.SaleReturnTitle; _purchaseReturnTitle.Text=value.PurchaseReturnTitle;
        _deliveryTitle.Text=value.DeliveryTitle; _cashBankTitle.Text=value.CashBankTitle; _signature1.Text=value.Signature1; _signature2.Text=value.Signature2; _signature3.Text=value.Signature3; _footer.Text=value.PrintFooterNote;
    }

    private static int Integer(TextBox box, string label) => int.TryParse(box.Text, out var value) ? value : throw new InvalidOperationException($"{label} harus berupa angka.");
    private static decimal Decimal(TextBox box, string label) => decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : throw new InvalidOperationException($"{label} harus berupa angka.");
    private static string? Empty(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    internal static TextBlock Header(string text) => new() { Text=text, FontSize=22, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(110,16,30)), Margin=new Thickness(18,15,18,8) };
    internal static TextBox Box(string text="", bool multiline=false) => new()
    {
        Text = text,
        MinWidth = 240,
        Height = multiline ? 64 : 27,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
        VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left,
        Padding = new Thickness(6, 3, 6, 3)
    };
    internal static CheckBox Check(string text) => new() { Content=text, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,4,0,4) };
    internal static ComboBox Combo(params string[] values) => new() { ItemsSource=values, SelectedIndex=0, Height=27, MinWidth=240 };
    internal static Button Button(string text, double width=92) => new() { Content=text, Width=width, Height=30, Margin=new Thickness(5,0,0,0), Padding=new Thickness(8,2,8,2) };
    internal static TabItem Tab(string title, object content) => new() { Header=title, Content=content, Padding=new Thickness(9,4,9,4) };
    internal static Grid FormGrid() { var g=new Grid { Margin=new Thickness(18) }; g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(190)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)}); return g; }
    internal static void AddRow(Grid grid, string label, UIElement input, double height=40) { var row=grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(height)}); var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,6,14,0)}; Grid.SetRow(text,row); Grid.SetColumn(text,0); Grid.SetRow(input,row); Grid.SetColumn(input,1); grid.Children.Add(text); grid.Children.Add(input); }
    internal static ScrollViewer Scroll(UIElement value) => new() { Content=value, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
    internal static StackPanel Vertical(params UIElement[] values) { var panel=new StackPanel(); foreach(var value in values) panel.Children.Add(value); return panel; }
}

internal sealed class UserManagementView : UserControl
{
    private readonly AksaraApiClient _api=AksaraApiClient.Shared;
    private readonly ObservableCollection<AppUserDto> _users=[];
    private readonly DataGrid _grid=new() { AutoGenerateColumns=false, IsReadOnly=true, SelectionMode=DataGridSelectionMode.Single, CanUserAddRows=false, HeadersVisibility=DataGridHeadersVisibility.Column, GridLinesVisibility=DataGridGridLinesVisibility.All };
    private IReadOnlyList<AppRoleDto> _roles=[];
    public event EventHandler? CloseRequested;

    public UserManagementView()
    {
        _grid.Columns.Add(new DataGridTextColumn{Header="Username",Binding=new Binding(nameof(AppUserDto.UserName)),Width=150});
        _grid.Columns.Add(new DataGridTextColumn{Header="Nama Pengguna",Binding=new Binding(nameof(AppUserDto.DisplayName)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        _grid.Columns.Add(new DataGridTextColumn{Header="Grup",Binding=new Binding(nameof(AppUserDto.RoleName)),Width=190});
        _grid.Columns.Add(new DataGridCheckBoxColumn{Header="Aktif",Binding=new Binding(nameof(AppUserDto.IsActive)),Width=70});
        _grid.Columns.Add(new DataGridTextColumn{Header="Login Terakhir",Binding=new Binding(nameof(AppUserDto.LastLoginAt)){StringFormat="dd/MM/yyyy HH:mm"},Width=145});
        _grid.ItemsSource=_users; _grid.MouseDoubleClick+=(_,_)=>EditSelected();
        var root=new DockPanel{Background=Brushes.White}; var title=ProgramConfigurationView.Header("Manajemen Pengguna"); DockPanel.SetDock(title,Dock.Top); root.Children.Add(title);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};
        var add=ProgramConfigurationView.Button("Tambah"); var edit=ProgramConfigurationView.Button("Edit"); var refresh=ProgramConfigurationView.Button("Muat Ulang",100); var close=ProgramConfigurationView.Button("Tutup");
        add.IsEnabled=CanManageAccess(); edit.IsEnabled=add.IsEnabled;
        add.Click+=(_,_)=>OpenEditor(null); edit.Click+=(_,_)=>EditSelected(); refresh.Click+=async(_,_)=>await LoadAsync(); close.Click+=(_,_)=>CloseRequested?.Invoke(this,EventArgs.Empty);
        actions.Children.Add(add); actions.Children.Add(edit); actions.Children.Add(refresh); actions.Children.Add(close); DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        _grid.Margin=new Thickness(14,6,14,0); root.Children.Add(_grid); Content=root; Loaded+=async(_,_)=>await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { IsEnabled=false; _roles=await _api.GetRolesAsync(); var rows=await _api.GetUsersAsync(); _users.Clear(); foreach(var row in rows)_users.Add(row); }
        catch(Exception exception){MessageBox.Show(exception.Message,"Daftar User",MessageBoxButton.OK,MessageBoxImage.Warning);} finally{IsEnabled=true;}
    }
    private void EditSelected(){if(_grid.SelectedItem is AppUserDto row)OpenEditor(row); else MessageBox.Show("Pilih pengguna yang akan diedit.","Daftar User",MessageBoxButton.OK,MessageBoxImage.Information);}
    private async void OpenEditor(AppUserDto? row)
    {
        if(!CanManageAccess()){MessageBox.Show("Pengguna ini hanya memiliki akses melihat daftar user.","Daftar User",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        var isSuperadmin=_api.CurrentRoleCode.Equals("MASTER",StringComparison.OrdinalIgnoreCase);
        if(!isSuperadmin&&row?.RoleCode.Equals("MASTER",StringComparison.OrdinalIgnoreCase)==true){MessageBox.Show("Akun Superadmin hanya dapat diubah oleh Superadmin.","Daftar User",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        var dialog=new UserEditorWindow(row,_roles.Where(x=>(x.IsActive||x.Id==row?.RoleId)&&(isSuperadmin||!x.Code.Equals("MASTER",StringComparison.OrdinalIgnoreCase))).ToArray()){Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()!=true||dialog.Request is null)return;
        try{IsEnabled=false;await _api.SaveUserAsync(dialog.Request);await LoadAsync();}catch(Exception exception){MessageBox.Show(exception.Message,"Daftar User",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{IsEnabled=true;}
    }
    private bool CanManageAccess()=>_api.Can("CONFIGURATION","MANAGE")&&(_api.CurrentRoleCode.Equals("MASTER",StringComparison.OrdinalIgnoreCase)||_api.CurrentRoleCode.Equals("ADMIN",StringComparison.OrdinalIgnoreCase));
}

internal sealed class UserEditorWindow : Window
{
    private readonly TextBox _user=ProgramConfigurationView.Box();
    private readonly TextBox _display=ProgramConfigurationView.Box();
    private readonly PasswordBox _password=new(){Height=27,MinWidth=240,Padding=new Thickness(6,3,6,3)};
    private readonly ComboBox _role=new(){Height=27,MinWidth=240,DisplayMemberPath=nameof(AppRoleDto.Name),SelectedValuePath=nameof(AppRoleDto.Id)};
    private readonly CheckBox _active=ProgramConfigurationView.Check("Pengguna aktif");
    private readonly AppUserDto? _source;
    public SaveAppUserRequest? Request{get;private set;}
    public UserEditorWindow(AppUserDto? source,IReadOnlyList<AppRoleDto> roles)
    {
        _source=source; Title=source is null?"Tambah User":"Edit User"; Width=500;Height=340;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _role.ItemsSource=roles; var grid=ProgramConfigurationView.FormGrid(); Add(grid,"Username",_user);Add(grid,"Nama Pengguna",_display);Add(grid,source is null?"Password":"Password Baru",_password);Add(grid,"Grup",_role);Add(grid,"Status",_active);
        if(source is not null){_user.Text=source.UserName;_display.Text=source.DisplayName;_role.SelectedValue=source.RoleId;_active.IsChecked=source.IsActive;}else{_role.SelectedItem=roles.FirstOrDefault(x=>x.Code.Equals("STAFF",StringComparison.OrdinalIgnoreCase))??roles.FirstOrDefault();_active.IsChecked=true;}
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};var save=ProgramConfigurationView.Button("Simpan");var cancel=ProgramConfigurationView.Button("Batal");save.Click+=Save;cancel.Click+=(_,_)=>DialogResult=false;buttons.Children.Add(save);buttons.Children.Add(cancel);
        var root=new DockPanel{Background=Brushes.White};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);root.Children.Add(grid);Content=root;
    }
    private static void Add(Grid grid,string label,UIElement input)=>ProgramConfigurationView.AddRow(grid,label,input);
    private void Save(object sender,RoutedEventArgs e){if(_role.SelectedValue is not Guid role){MessageBox.Show("Pilih grup pengguna.",Title);return;}Request=new(_source?.Id,_user.Text,_display.Text,role,string.IsNullOrWhiteSpace(_password.Password)?null:_password.Password,_active.IsChecked==true,_source?.Version);DialogResult=true;}
}

internal sealed class AccessConfigurationView : UserControl
{
    private readonly AksaraApiClient _api=AksaraApiClient.Shared;
    private readonly ComboBox _roles=new(){Width=260,Height=28,DisplayMemberPath=nameof(AppRoleDto.Name),SelectedValuePath=nameof(AppRoleDto.Id)};
    private readonly ObservableCollection<EditablePermission> _permissions=[];
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,CanUserAddRows=false,GridLinesVisibility=DataGridGridLinesVisibility.All,HeadersVisibility=DataGridHeadersVisibility.Column};
    private IReadOnlyList<AppRoleDto> _roleRows=[];
    private readonly Button _save=ProgramConfigurationView.Button("Simpan Akses",112);
    public event EventHandler? CloseRequested;
    public AccessConfigurationView()
    {
        _grid.Columns.Add(new DataGridTextColumn{Header="Menu / Modul",Binding=new Binding(nameof(EditablePermission.ModuleName)),IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        _grid.Columns.Add(new DataGridCheckBoxColumn{Header="Lihat",Binding=new Binding(nameof(EditablePermission.CanView)){Mode=BindingMode.TwoWay},Width=90});
        _grid.Columns.Add(new DataGridCheckBoxColumn{Header="Tambah / Edit",Binding=new Binding(nameof(EditablePermission.CanManage)){Mode=BindingMode.TwoWay},Width=120});
        _grid.Columns.Add(new DataGridCheckBoxColumn{Header="Hapus / Pulihkan",Binding=new Binding(nameof(EditablePermission.CanDelete)){Mode=BindingMode.TwoWay},Width=130});
        _grid.ItemsSource=_permissions;
        var root=new DockPanel{Background=Brushes.White};var title=ProgramConfigurationView.Header("Grup & Hak Akses");DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var top=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(14,6,14,8)};top.Children.Add(new TextBlock{Text="Grup User",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)});top.Children.Add(_roles);var add=ProgramConfigurationView.Button("Tambah Grup",112);var edit=ProgramConfigurationView.Button("Edit Grup",100);top.Children.Add(add);top.Children.Add(edit);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        add.IsEnabled=CanManageAccess();edit.IsEnabled=add.IsEnabled;_save.IsEnabled=false;_grid.IsReadOnly=!add.IsEnabled;
        var bottom=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};var close=ProgramConfigurationView.Button("Tutup");bottom.Children.Add(_save);bottom.Children.Add(close);DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        _grid.Margin=new Thickness(14,0,14,0);root.Children.Add(_grid);Content=root;
        _roles.SelectionChanged+=async(_,_)=>await LoadPermissionsAsync();add.Click+=(_,_)=>OpenRoleEditor(null);edit.Click+=(_,_)=>OpenRoleEditor(_roles.SelectedItem as AppRoleDto);_save.Click+=async(_,_)=>await SaveAsync();close.Click+=(_,_)=>CloseRequested?.Invoke(this,EventArgs.Empty);Loaded+=async(_,_)=>await LoadRolesAsync();
    }
    private async Task LoadRolesAsync(Guid? select=null){try{IsEnabled=false;_roleRows=await _api.GetRolesAsync();_roles.ItemsSource=_roleRows;_roles.SelectedValue=select??_roleRows.FirstOrDefault()?.Id;}catch(Exception ex){MessageBox.Show(ex.Message,"Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{IsEnabled=true;}}
    private async Task LoadPermissionsAsync(){_save.IsEnabled=false;_grid.IsReadOnly=true;if(_roles.SelectedValue is not Guid id)return;try{IsEnabled=false;var rows=await _api.GetRolePermissionsAsync(id);_permissions.Clear();foreach(var row in rows)_permissions.Add(new(row));var master=(_roles.SelectedItem as AppRoleDto)?.Code.Equals("MASTER",StringComparison.OrdinalIgnoreCase)==true;var canManage=CanManageAccess();_grid.IsReadOnly=master||!canManage;_save.IsEnabled=!master&&canManage;}catch(Exception ex){MessageBox.Show(ex.Message,"Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{IsEnabled=true;}}
    private async Task SaveAsync(){if(_roles.SelectedValue is not Guid id||!CanManageAccess()||_grid.IsReadOnly)return;try{IsEnabled=false;await _api.SaveRolePermissionsAsync(id,_permissions.Select(x=>x.ToDto()).ToArray());MessageBox.Show("Hak akses grup berhasil disimpan. Pengguna terkait akan memperoleh akses baru saat login berikutnya.","Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception ex){MessageBox.Show(ex.Message,"Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{IsEnabled=true;}}
    private async void OpenRoleEditor(AppRoleDto? row){if(!CanManageAccess())return;if(row?.IsSystem==true){MessageBox.Show("Nama grup bawaan tetap. Hak akses Admin, Manajemen, dan Staff dapat diubah pada tabel izin.","Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Information);return;}var dialog=new RoleEditorWindow(row){Owner=Window.GetWindow(this)};if(dialog.ShowDialog()!=true||dialog.Request is null)return;try{var saved=await _api.SaveRoleAsync(dialog.Request);await LoadRolesAsync(saved.Id);}catch(Exception ex){MessageBox.Show(ex.Message,"Konfigurasi Akses",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private bool CanManageAccess()=>_api.Can("CONFIGURATION","MANAGE")&&(_api.CurrentRoleCode.Equals("MASTER",StringComparison.OrdinalIgnoreCase)||_api.CurrentRoleCode.Equals("ADMIN",StringComparison.OrdinalIgnoreCase));
}

internal sealed class EditablePermission : INotifyPropertyChanged
{
    private bool _view,_manage,_delete;
    public string Module{get;} public string ModuleName{get;} public bool CanView{get=>_view;set{_view=value;Changed();}} public bool CanManage{get=>_manage;set{_manage=value;if(value)_view=true;Changed();Changed(nameof(CanView));}} public bool CanDelete{get=>_delete;set{_delete=value;if(value)_view=true;Changed();Changed(nameof(CanView));}}
    public EditablePermission(RolePermissionDto value){Module=value.Module;ModuleName=value.ModuleName;_view=value.CanView;_manage=value.CanManage;_delete=value.CanDelete;}
    public RolePermissionDto ToDto()=>new(Module,ModuleName,CanView,CanManage,CanDelete);
    public event PropertyChangedEventHandler? PropertyChanged;private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}

internal sealed class RoleEditorWindow : Window
{
    private readonly TextBox _code=ProgramConfigurationView.Box();private readonly TextBox _name=ProgramConfigurationView.Box();private readonly CheckBox _active=ProgramConfigurationView.Check("Grup aktif");private readonly AppRoleDto? _source;public SaveAppRoleRequest? Request{get;private set;}
    public RoleEditorWindow(AppRoleDto? source){_source=source;Title=source is null?"Tambah Grup User":"Edit Grup User";Width=490;Height=255;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;var grid=ProgramConfigurationView.FormGrid();ProgramConfigurationView.AddRow(grid,"Kode Grup",_code);ProgramConfigurationView.AddRow(grid,"Nama Grup",_name);ProgramConfigurationView.AddRow(grid,"Status",_active);if(source is not null){_code.Text=source.Code;_code.IsReadOnly=true;_name.Text=source.Name;_active.IsChecked=source.IsActive;_active.IsEnabled=!source.IsSystem;}else _active.IsChecked=true;var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};var save=ProgramConfigurationView.Button("Simpan");var cancel=ProgramConfigurationView.Button("Batal");save.Click+=(_,_)=>{Request=new(_source?.Id,_code.Text,_name.Text,_active.IsChecked==true,_source?.Version);DialogResult=true;};cancel.Click+=(_,_)=>DialogResult=false;buttons.Children.Add(save);buttons.Children.Add(cancel);var root=new DockPanel{Background=Brushes.White};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);root.Children.Add(grid);Content=root;}
}

internal sealed class ChangeCredentialsView : UserControl
{
    private readonly TextBox _user=ProgramConfigurationView.Box();private readonly TextBox _display=ProgramConfigurationView.Box();private readonly PasswordBox _current=new(){Height=27,MinWidth=240,Padding=new Thickness(6,3,6,3)};private readonly PasswordBox _next=new(){Height=27,MinWidth=240,Padding=new Thickness(6,3,6,3)};private readonly PasswordBox _confirm=new(){Height=27,MinWidth=240,Padding=new Thickness(6,3,6,3)};
    public event EventHandler? CloseRequested;public event EventHandler? CredentialsChanged;
    public ChangeCredentialsView(){_user.Text=AksaraApiClient.Shared.CurrentUserName;_display.Text=AksaraApiClient.Shared.CurrentDisplayName;var root=new DockPanel{Background=Brushes.White};var title=ProgramConfigurationView.Header("Ubah Nama / Password");DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};var save=ProgramConfigurationView.Button("Simpan");var close=ProgramConfigurationView.Button("Tutup");save.Click+=async(_,_)=>await SaveAsync();close.Click+=(_,_)=>CloseRequested?.Invoke(this,EventArgs.Empty);buttons.Children.Add(save);buttons.Children.Add(close);DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);var grid=ProgramConfigurationView.FormGrid();ProgramConfigurationView.AddRow(grid,"Username",_user);ProgramConfigurationView.AddRow(grid,"Nama Pengguna",_display);ProgramConfigurationView.AddRow(grid,"Password Saat Ini",_current);ProgramConfigurationView.AddRow(grid,"Password Baru",_next);ProgramConfigurationView.AddRow(grid,"Ulangi Password Baru",_confirm);root.Children.Add(grid);Content=root;}
    private async Task SaveAsync(){if(_next.Password!=_confirm.Password){MessageBox.Show("Konfirmasi password baru tidak sama.","Ubah Nama / Password",MessageBoxButton.OK,MessageBoxImage.Warning);return;}try{IsEnabled=false;await AksaraApiClient.Shared.ChangeCredentialsAsync(new(_user.Text,_display.Text,_current.Password,string.IsNullOrWhiteSpace(_next.Password)?null:_next.Password));_current.Clear();_next.Clear();_confirm.Clear();CredentialsChanged?.Invoke(this,EventArgs.Empty);MessageBox.Show("Nama pengguna dan password berhasil diperbarui.","Ubah Nama / Password",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception ex){MessageBox.Show(ex.Message,"Ubah Nama / Password",MessageBoxButton.OK,MessageBoxImage.Warning);}finally{IsEnabled=true;}}
}

internal sealed class ConnectionConfigurationView : UserControl
{
    private readonly TextBox _address=ProgramConfigurationView.Box();private readonly TextBlock _status=new(){Margin=new Thickness(0,8,0,0),Foreground=Brushes.DimGray};public event EventHandler? CloseRequested;
    public ConnectionConfigurationView(){_address.Text=AksaraApiClient.Shared.ServerAddress.ToString().TrimEnd('/');var root=new DockPanel{Background=Brushes.White};var title=ProgramConfigurationView.Header("Konfigurasi Koneksi Server");DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12)};var test=ProgramConfigurationView.Button("Uji Koneksi",105);var save=ProgramConfigurationView.Button("Simpan",92);var close=ProgramConfigurationView.Button("Tutup",92);test.Click+=async(_,_)=>await TestAsync();save.Click+=(_,_)=>Save();close.Click+=(_,_)=>CloseRequested?.Invoke(this,EventArgs.Empty);buttons.Children.Add(test);buttons.Children.Add(save);buttons.Children.Add(close);DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);var grid=ProgramConfigurationView.FormGrid();ProgramConfigurationView.AddRow(grid,"Alamat Server",_address);var panel=new StackPanel();panel.Children.Add(new TextBlock{Text="Koneksi ini berlaku khusus untuk komputer ini.",Foreground=Brushes.DimGray});panel.Children.Add(_status);ProgramConfigurationView.AddRow(grid,"Status",panel,64);root.Children.Add(grid);Content=root;}
    private async Task TestAsync(){try{IsEnabled=false;_status.Text="Menguji koneksi...";var ok=await AksaraApiClient.TestServerAddressAsync(_address.Text);_status.Text=ok?"Server dapat dihubungi.":"Server belum dapat dihubungi.";}catch(Exception ex){_status.Text=ex.Message;}finally{IsEnabled=true;}}
    private void Save(){try{AksaraApiClient.SaveLocalServerAddress(_address.Text);_status.Text="Alamat tersimpan. Perubahan dipakai saat login berikutnya.";MessageBox.Show(_status.Text,"Konfigurasi Koneksi Server",MessageBoxButton.OK,MessageBoxImage.Information);}catch(Exception ex){MessageBox.Show(ex.Message,"Konfigurasi Koneksi Server",MessageBoxButton.OK,MessageBoxImage.Warning);}}
}

using Aksara.Contracts;

namespace Aksara.Desktop.Views;

internal sealed class SupplierLookupWindow : SalesLookupWindowBase<PurchaseSupplierDto>
{
    public PurchaseSupplierDto? SelectedSupplier { get; private set; }
    public SupplierLookupWindow() : base("FORM CARI SUPPLIER", 780, 500)
    {
        Grid.Columns.Add(Column("Kode", nameof(PurchaseSupplierDto.Code), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(PurchaseSupplierDto.CompanyName), 260));
        Grid.Columns.Add(Column("Alamat", nameof(PurchaseSupplierDto.Address), 300));
        Grid.Columns.Add(Column("Tempo", nameof(PurchaseSupplierDto.CreditDays), 65));
        AddLookupAction("Tambah", () => OpenEditorAsync(null));
        AddLookupAction("Ubah", EditSelectedAsync);
    }
    protected override Task<IReadOnlyList<PurchaseSupplierDto>> LoadAsync(string query) => Api.SearchSuppliersAsync(query);
    protected override void SetSelected(PurchaseSupplierDto selected) => SelectedSupplier = selected;
    private async Task EditSelectedAsync()
    {
        if (Grid.SelectedItem is not PurchaseSupplierDto selected) return;
        var row = (await Api.GetSuppliersAsync(selected.Code, true)).FirstOrDefault(x => string.Equals(x.Code, selected.Code, StringComparison.OrdinalIgnoreCase));
        await OpenEditorAsync(row);
    }
    private async Task OpenEditorAsync(SupplierMasterDto? row)
    {
        var editor = new BusinessPartnerEditWindow(BusinessPartnerKind.Supplier, row) { Owner = this };
        if (editor.ShowDialog() != true) return;
        SearchBox.Text = editor.SavedCode ?? SearchBox.Text;
        await ReloadAsync();
    }
}

internal sealed class PurchaseItemLookupWindow(string warehouseCode, bool serviceMode = false) : SalesLookupWindowBase<PurchaseItemDto>(serviceMode ? "FORM CARI JASA PEMBELIAN" : "FORM CARI BARANG PEMBELIAN", 860, 540)
{
    public PurchaseItemDto? SelectedItem { get; private set; }
    protected override Task<IReadOnlyList<PurchaseItemDto>> LoadAsync(string query) => Api.SearchPurchaseItemsAsync(query, warehouseCode, itemType: serviceMode ? "SERVICE" : "GOODS");
    protected override void SetSelected(PurchaseItemDto selected) => SelectedItem = selected;
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Grid.Columns.Add(Column(serviceMode ? "Kode Jasa" : "Kode Barang", nameof(PurchaseItemDto.Code), 130));
        Grid.Columns.Add(Column(serviceMode ? "Nama Jasa" : "Nama Barang", nameof(PurchaseItemDto.Name), 310));
        if (!serviceMode) Grid.Columns.Add(Column("Stok", nameof(PurchaseItemDto.AvailableStock), 90));
        Grid.Columns.Add(Column("Sat.", nameof(PurchaseItemDto.Unit), 70));
        Grid.Columns.Add(Column("Harga Beli", nameof(PurchaseItemDto.PurchasePrice), 110));
        Grid.Columns.Add(Column("Gudang", nameof(PurchaseItemDto.WarehouseCode), 90));
        AddLookupAction(serviceMode ? "Tambah Jasa" : "Tambah Barang", OpenItemEditorAsync, 108);
    }
    private async Task OpenItemEditorAsync()
    {
        var editor = new ItemDetailWindow(serviceMode: serviceMode) { Owner = this };
        if (editor.ShowDialog() == true) await ReloadAsync();
    }
}

internal sealed class PurchaseLookupWindow : SalesLookupWindowBase<PurchaseDto>
{
    private readonly bool _serviceMode;
    public PurchaseDto? SelectedPurchase { get; private set; }
    public PurchaseLookupWindow(bool serviceMode = false) : base(serviceMode ? "FORM CARI PEMBELIAN JASA" : "FORM CARI PEMBELIAN", 930, 540)
    {
        _serviceMode = serviceMode;
        Grid.Columns.Add(Column("No. Faktur", nameof(PurchaseDto.DocumentNumber), 140));
        Grid.Columns.Add(Column("Tanggal", nameof(PurchaseDto.DocumentDate), 100));
        Grid.Columns.Add(Column("Kode Supl.", nameof(PurchaseDto.SupplierCode), 110));
        Grid.Columns.Add(Column("Toko/PT", nameof(PurchaseDto.SupplierName), 230));
        Grid.Columns.Add(Column("Total", nameof(PurchaseDto.GrandTotal), 120));
        Grid.Columns.Add(Column("Status", nameof(PurchaseDto.Status), 100));
    }
    protected override Task<IReadOnlyList<PurchaseDto>> LoadAsync(string query) => Api.SearchPurchasesAsync(query, documentKind: _serviceMode ? "SERVICE" : "GOODS");
    protected override void SetSelected(PurchaseDto selected) => SelectedPurchase = selected;
}

internal sealed class PurchaseReturnLookupWindow : SalesLookupWindowBase<PurchaseReturnDto>
{
    public PurchaseReturnDto? SelectedReturn { get; private set; }
    public PurchaseReturnLookupWindow() : base("FORM CARI RETUR PEMBELIAN", 940, 540)
    {
        Grid.Columns.Add(Column("No. Retur", nameof(PurchaseReturnDto.DocumentNumber), 130));
        Grid.Columns.Add(Column("Tanggal", nameof(PurchaseReturnDto.DocumentDate), 100));
        Grid.Columns.Add(Column("No. Faktur", nameof(PurchaseReturnDto.SourceDocumentNumber), 130));
        Grid.Columns.Add(Column("Kode Supl.", nameof(PurchaseReturnDto.SupplierCode), 100));
        Grid.Columns.Add(Column("Toko/PT", nameof(PurchaseReturnDto.SupplierName), 220));
        Grid.Columns.Add(Column("Total", nameof(PurchaseReturnDto.GrandTotal), 110));
        Grid.Columns.Add(Column("Status", nameof(PurchaseReturnDto.Status), 90));
    }
    protected override Task<IReadOnlyList<PurchaseReturnDto>> LoadAsync(string query) => Api.SearchPurchaseReturnsAsync(query);
    protected override void SetSelected(PurchaseReturnDto selected) => SelectedReturn = selected;
}

using System.Windows;
using System.Windows.Controls;

namespace Aksara.Desktop.Views;

public sealed class ServiceSalesView : SalesView
{
    public ServiceSalesView() : base(serviceMode: true)
    {
        StandardSalesHeader.Visibility = Visibility.Collapsed;
        ServiceSalesHeader.Visibility = Visibility.Visible;
        SalesHeaderRow.Height = new GridLength(64);
        DetailTabs.Visibility = Visibility.Collapsed;
        ServiceDetailsHeading.Visibility = Visibility.Visible;

        DocumentDateLabel.Text = "Tanggal";
        DocumentNumberLabel.Text = "No. Jasa";
        CustomerCodeLabel.Text = "Customer";
        CustomerNameLabel.Text = "Nama";
        TransactionTypeLabel.Text = "Bayar";
        DueDateLabel.Text = "Jatuh Tempo";
        DueDaysLabel.Text = "Hari";
        SalespersonLabel.Text = "Sales";
        CustomerAddressLabel.Text = "Alamat";

        LinesGrid.Columns[0].Visibility = Visibility.Collapsed;
        LinesGrid.Columns[1].Header = "Kode Jasa";
        LinesGrid.Columns[1].Width = new DataGridLength(145);
        LinesGrid.Columns[2].Header = "Uraian Jasa";
        LinesGrid.Columns[2].MinWidth = 380;
        LinesGrid.Columns[4].Header = "Jumlah";
        LinesGrid.Columns[4].Width = new DataGridLength(90);
        LinesGrid.Columns[5].Visibility = Visibility.Collapsed;
        LinesGrid.Columns[6].Header = "Tarif";
        LinesGrid.Columns[6].Width = new DataGridLength(120);
        LinesGrid.Columns[7].Header = "Diskon";
        LinesGrid.Columns[7].Width = new DataGridLength(85);
        LinesGrid.Columns[8].Header = "Nilai Jasa";
        LinesGrid.Columns[8].Width = new DataGridLength(130);

        HppGrid.Columns[0].Header = "Kode Jasa";
        HppGrid.Columns[1].Header = "Uraian Jasa";
        HppGrid.Columns[2].Header = "Jumlah";
        HppGrid.Columns[3].Header = "Satuan";
        HppGrid.Columns[4].Header = "Biaya Bahan / Sat.";

        CttnGrid.Columns[0].Header = "Kode Jasa";
        CttnGrid.Columns[1].Header = "Uraian Jasa";
        CttnGrid.Columns[2].Header = "Jumlah";
        CttnGrid.Columns[3].Header = "Satuan";
        CttnGrid.Columns[4].Header = "Uraian Pekerjaan";

        NotesPanelTitle.Text = "Catatan Pekerjaan";
        TransactionCalculationTitle.Text = "Nilai Transaksi Jasa";
        PaymentSummaryTitle.Text = "Pembayaran Jasa";
        NetLabel.Text = "Nilai Bersih";
        TotalLabel.Text = "Total Tagihan";
    }
}

# AKSARA

AKSARA adalah aplikasi akuntansi dan administrasi berbasis desktop untuk membantu pencatatan transaksi, persediaan, kas dan bank, utang-piutang, buku besar, serta pelaporan usaha.

## Teknologi

- .NET 10
- WPF untuk aplikasi desktop
- ASP.NET Core untuk API/server
- PostgreSQL untuk penyimpanan data

## Modul utama

- Penjualan dan retur penjualan
- Pembelian dan retur pembelian
- Persediaan, gudang, dan kartu stok
- Kas, bank, penerimaan, dan pembayaran
- Supplier, customer, utang, dan piutang
- Ledger dan laporan akuntansi
- Penjualan dan master jasa
- Tutup buku, pembatalan tutup buku, dan audit periode
- Setup data awal dan dukungan beberapa perusahaan

## Struktur proyek

- `Aksara.Desktop`: aplikasi desktop WPF
- `Aksara.Server`: API dan distribusi pembaruan
- `Aksara.Infrastructure`: repository, integrasi database, dan migration
- `Aksara.Domain`: aturan bisnis
- `Aksara.Contracts`: kontrak data antara desktop dan server
- `Aksara.Provisioning`: alat provisioning perusahaan baru
- `Aksara.Qa`: pemeriksaan regresi utama

## Menjalankan pemeriksaan

```powershell
dotnet run --project .\Aksara.Qa\Aksara.Qa.csproj -c Release
```

## Build aplikasi desktop

```powershell
dotnet build .\Aksara.Desktop\Aksara.Desktop.csproj -c Release
```

Salin `Aksara.Desktop/aksara-client.example.json` menjadi `Aksara.Desktop/aksara-client.json`, lalu sesuaikan alamat server untuk lingkungan pengembangan. File konfigurasi lokal tersebut sengaja tidak disimpan di repository.

## Menjalankan server

Sediakan PostgreSQL dan isi connection string melalui konfigurasi aman atau environment variable `ConnectionStrings__Aksara`, kemudian jalankan:

```powershell
dotnet run --project .\Aksara.Server\Aksara.Server.csproj
```

Jangan menyimpan password database, service account, atau kredensial pengguna di repository.

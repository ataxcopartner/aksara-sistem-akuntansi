# Provisioning perusahaan baru

Alat ini hanya untuk database perusahaan baru. Ia menolak database yang sudah berisi tabel tanpa `aksara_provisioning_guard`, identitas perusahaan lain, atau setup yang sudah aktif.

Username bukan penentu tenant. Tenant dipilih dari profil perusahaan yang berisi URL server tersendiri; server tersebut harus diarahkan hanya ke database perusahaan baru. Username yang sama pada dua database tetap merupakan dua akun berbeda.

Tinjau rencana tanpa koneksi atau perubahan database terlebih dahulu:

```powershell
dotnet run --project .\Aksara.Provisioning\Aksara.Provisioning.csproj -- `
  --plan true `
  --server-url "https://aksara-perusahaan-baru.example.com" `
  --superadmin-user admin-baru `
  --superadmin-name "Superadmin Perusahaan Baru"
```

`--company-name` tidak wajib. Jika tidak diisi, provisioning memakai label sementara `Perusahaan Baru (Belum Setup)`. Nama perusahaan final wajib diisi dan disimpan melalui **Setup Data Awal > Profil Perusahaan** sebelum aktivasi. Walaupun nama diberikan saat provisioning, pengguna tetap harus membuka dan menyimpan Profil Perusahaan untuk mengonfirmasinya. Label sementara maupun nama yang belum dikonfirmasi tidak dapat lolos preflight atau aktivasi.

`--company-code` dan `--database` adalah identitas teknis yang terpisah dari nama perusahaan. Keduanya boleh ditentukan operator. Jika tidak diberikan, provisioning membuat nilai teknis baru dan menampilkannya pada hasil plan; gunakan nilai eksplisit pada eksekusi nyata apabila nama teknis perlu ditetapkan organisasi.

Password tidak boleh dikirim sebagai argumen. Atur koneksi administrator dan password melalui secret environment pada sesi terminal, jalankan provisioning, lalu hapus variabel dari sesi tersebut.

Username bootstrap harus unik pada seluruh direktori tenant. Setelah provisioning, daftarkan username, URL server, kode perusahaan, dan instance ID yang dicetak alat ini pada `TenantDirectory` server bootstrap utama. Desktop kemudian menentukan perusahaan otomatis dari username; pengguna tidak memilih profil atau endpoint.

```powershell
$env:AKSARA_PROVISION_ADMIN_CONNECTION = '<connection string administrator PostgreSQL>'
$env:AKSARA_BOOTSTRAP_PASSWORD = '<password awal yang kuat>'
dotnet run --project .\Aksara.Provisioning\Aksara.Provisioning.csproj -- `
  --database aksara_perusahaan_baru `
  --company-code PERUSAHAAN-BARU `
  --server-url "https://aksara-perusahaan-baru.example.com" `
  --superadmin-user admin-baru `
  --superadmin-name "Superadmin Perusahaan Baru"
Remove-Item Env:AKSARA_BOOTSTRAP_PASSWORD
Remove-Item Env:AKSARA_PROVISION_ADMIN_CONNECTION
```

Password bootstrap minimal enam karakter agar akun awal yang ditentukan operator dapat dipertahankan persis. Password di database selalu disimpan sebagai hash. Password di bawah sepuluh karakter menghasilkan peringatan dan sebaiknya langsung diganti setelah login pertama.

Setelah berhasil, buat endpoint server terpisah yang hanya menggunakan connection string database baru tersebut. Jangan arahkan endpoint perusahaan lama ke database ini. Daftarkan username dan endpoint itu pada `TenantDirectory` server bootstrap; jangan menambahkan profil perusahaan pada desktop. Login pertama tetap diarahkan ke Setup Data Awal; isi nama legal/final pada Profil Perusahaan. Penyimpanan Profil Perusahaan menyinkronkan nama tersebut ke identitas instance. Database baru tidak mengambil transaksi atau master perusahaan lama; isinya hanya schema/migration, system seed, identitas perusahaan, akun bootstrap, dan setup `DRAFT`.

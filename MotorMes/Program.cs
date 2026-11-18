using System.Net.Sockets;
using NModbus;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Habilitar archivos estáticos para servir nuestro Dashboard HTML
app.UseDefaultFiles();
app.UseStaticFiles();

Console.WriteLine("=== INICIANDO SISTEMA MES (Nivel 3) ===");

// 1. Inicializar Base de Datos SQL para Trazabilidad
string connectionString = "Data Source=trazabilidad_mes.db";
using (var conn = new SqliteConnection(connectionString))
{
	conn.Open();
	var cmd = conn.CreateCommand();
	cmd.CommandText = @"
		CREATE TABLE IF NOT EXISTS ProduccionLog (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			FechaHora TEXT,
			NumeroSerie TEXT,
			Temperatura INTEGER,
			ResultadoCalidad TEXT
		);";
	cmd.ExecuteNonQuery();
}

ushort estadoActual = 0;
ushort piezasTotales = 0;
ushort piezasScrap = 0;
ushort ultimaTemperatura = 0;
int fallasConsecutivas = 0;
string mensajeAlerta = "Sistema Operando Normalmente";

// 2. Conexión Modbus TCP Compartida (con bloqueo para lectura/escritura segura)
using TcpClient client = new TcpClient("127.0.0.1", 5020);
var factory = new ModbusFactory();
IModbusMaster master = factory.CreateMaster(client);
SemaphoreSlim modbusLock = new SemaphoreSlim(1, 1);

ushort ultimoConteoPiezas = 0;

// 3. Hilo en Segundo Plano (Worker MES)
_ = Task.Run(async () =>
{
	ushort ultimoConteoPiezas = 0;

	while (true)
	{
		try
		{
			await modbusLock.WaitAsync();
			ushort[] registros;
			try
			{
				// Leer 4 registros (Holding Registers) empezando en la dirección 0
				registros = await master.ReadHoldingRegistersAsync(1, 0, 4);
			}
			finally
			{
				modbusLock.Release();
			}

			estadoActual = registros[0];
			piezasTotales = registros[1];
			piezasScrap = registros[2];
			ultimaTemperatura = registros[3];

			// Detectar si se produjo una pieza nueva (Flanco de subida)
			if (piezasTotales > ultimoConteoPiezas)
			{
				ultimoConteoPiezas = piezasTotales;
				string serie = $"SN-{DateTime.Now:yyyyMMdd}-{piezasTotales:D5}";
				bool esDefecto = ultimaTemperatura > 88;
				string calidad = esDefecto ? "RECHAZADA (ALTA TEMP)" : "APROBADA";

				if (esDefecto)
				{
					fallasConsecutivas++;
					// POKAYOKE ACTIVO: Detener PLC si hay 3 fallas seguidas
					if (fallasConsecutivas >= 3)
					{
						await modbusLock.WaitAsync();
						try
						{
							await master.WriteSingleRegisterAsync(1, 0, 0);
						}
						finally
						{
							modbusLock.Release();
						}
						estadoActual = 0;
						mensajeAlerta = "PARO AUTOMÁTICO (POKAYOKE): 3 defectos consecutivos por temperatura.";
						Console.ForegroundColor = ConsoleColor.Yellow;
						Console.WriteLine($"[POKAYOKE] {mensajeAlerta}");
						Console.ResetColor();
					}
				}
				else
				{
					fallasConsecutivas = 0;
					mensajeAlerta = "Sistema Operando Normalmente";
				}

				// Guardar trazabilidad en SQLite
				using var conn = new SqliteConnection(connectionString);
				conn.Open();
				var cmd = conn.CreateCommand();
				cmd.CommandText = "INSERT INTO ProduccionLog (FechaHora, NumeroSerie, Temperatura, ResultadoCalidad) VALUES ($fecha, $serie, $temp, $calidad)";
				cmd.Parameters.AddWithValue("$fecha", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				cmd.Parameters.AddWithValue("$serie", serie);
				cmd.Parameters.AddWithValue("$temp", ultimaTemperatura);
				cmd.Parameters.AddWithValue("$calidad", calidad);
				cmd.ExecuteNonQuery();

				Console.ForegroundColor = esDefecto ? ConsoleColor.Red : ConsoleColor.Green;
				Console.WriteLine($"[MES] {serie} | Temp: {ultimaTemperatura}°C | {calidad} | Fallas seguidas: {fallasConsecutivas}");
				Console.ResetColor();
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ERROR MODBUS]: {ex.Message}");
		}

		await Task.Delay(500);
}
});

// 4. Endpoints de la Minimal API para el Dashboard
app.MapGet("/api/status", () =>
{
	double calidadOEE = piezasTotales > 0 ? ((double)(piezasTotales - piezasScrap) / piezasTotales) * 100 : 100.0;
	return Results.Ok (new
	{
		estado = estadoActual,
		totales = piezasTotales,
		scrap = piezasScrap,
		temperatura = ultimaTemperatura,
		calidadOEE = Math.Round(calidadOEE, 1),
		fallasConsecutivas,
		alerta = mensajeAlerta
	});
});

app.MapGet("/api/logs", () =>
{
	var lista = new List<object>();
	using var conn = new SqliteConnection(connectionString);
	conn.Open();
	var cmd = conn.CreateCommand();
	cmd.CommandText = "SELECT FechaHora, NumeroSerie, Temperatura, ResultadoCalidad FROM ProduccionLog ORDER BY Id DESC LIMIT 12";
	using var reader = cmd.ExecuteReader();
	while (reader.Read())
	{
		lista.Add(new
		{
			fechaHora = reader.GetString(0),
			numeroSerie = reader.GetString(1),
			temperatura = reader.GetInt32(2),
			resultado = reader.GetString(3)
		});
	}
	return Results.Ok(lista);
});

app.MapPost("/api/control/{comando}", async (string comando) =>
{
	ushort valor = comando.ToLower() == "start" ? (ushort)1 : (ushort)0;
	await modbusLock.WaitAsync();
	try
	{
		await master.WriteSingleRegisterAsync(1, 0, valor);
	}
	finally
	{
		modbusLock.Release();
	}

	if (valor == 1)
	{
		fallasConsecutivas = 0;
		mensajeAlerta = "Arranque manual ejecutado por operador.";
	}
	else
	{
		mensajeAlerta = "Paro manual ejecutado desde Dashboard.";
	}
	estadoActual = valor;
	return Results.Ok(new { mensaje = mensajeAlerta, estado = valor });
});

Console.WriteLine("=== DASHBOARD MES DISPONIBLE EN: http://localhost:5000 ===");
app.Run("http://localhost:5000");

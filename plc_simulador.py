import time
import random
import threading
from pymodbus.server import StartTcpServer
from pymodbus.datastore import ModbusSequentialDataBlock, ModbusSlaveContext, ModbusServerContext

# Inicializar 10 registros Holding. Registro 0 inicia en 1 (Máquina Corriendo)
valores_iniciales = [1] + [0] * 9
store = ModbusSlaveContext(
    hr=ModbusSequentialDataBlock(1, valores_iniciales)
)
context = ModbusServerContext(slaves=store, single=True)

def simular_linea():
    piezas = 0
    scrap = 0
    while True:
        time.sleep(2)
        
        # Leer el registro 0 (código de función 3 = Holding Registers)
        estado_actual = store.getValues(3, 0, count=1).pop(0)
        
        if estado_actual == 0:
            print("[PLC] MÁQUINA DETENIDA POR SISTEMA MES / OPERADOR. Esperando arranque...")
            continue

        temp = random.randint(65, 96)
        piezas += 1
        estado_nuevo = 1
        
        if temp > 88:
            scrap += 1
            estado_nuevo = 2  # Alerta de temperatura alta
            
        # Actualizar registros: [Estado, PiezasTotales, Scrap, Temperatura]
        store.setValues(3, 0, [estado_nuevo, piezas, scrap, temp])
        print(f"[PLC] Estado: {estado_nuevo} | Total: {piezas} | Scrap: {scrap} | Temp: {temp}°C")

if __name__ == "__main__":
    hilo = threading.Thread(target=simular_linea, daemon=True)
    hilo.start()
    print("Iniciando PLC Virtual Bidireccional en 127.0.0.1:5020...")
    StartTcpServer(context=context, address=("127.0.0.1", 5020))

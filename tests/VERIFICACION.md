# Verificacion realizada

Comprobado con PostgreSQL 18 y ambos servicios .NET 10 en Docker.

- Compilacion Release de ambas imagenes sin errores.
- Prueba automatizada Probar.ps1 completa: CRUD, Proxy, dos observadores,
  Adapter, COMMIT, ROLLBACK tras debito y concurrencia.
- En concurrencia: saldo inicial 100, dos intentos de 80,
  exactamente un COMMIT, un rechazo y saldo origen 20.
- Circuit Breaker: tres errores -> OPEN; cuarta solicitud sin llamada externa;
  HALF-OPEN -> OPEN si sigue fallando; HALF-OPEN -> CLOSED al recuperar.
- Durabilidad: reinicio real de app y postgres; movimiento 1273 conservado,
  saldo cuenta 101 = 18, cuenta 102 = 82 y cuenta 103 = 0.
- /health volvio a responder {"status":"ok"} despues del reinicio.
- Backup Custom generado con scripts/Respaldar.ps1 y restaurado con
  scripts/Restaurar.ps1 en banking_restored: 1273 movimientos y 103 cuentas.
- Interfaz revisada en navegador: resumen y formulario de transferencia cargan.

La salida de la prueba automatizada esta en resultado-docker.txt.
Estos datos corresponden al momento de la prueba; al utilizar la aplicacion cambiaran.
El backup incluido conserva los datos de demostracion usados en la verificacion.

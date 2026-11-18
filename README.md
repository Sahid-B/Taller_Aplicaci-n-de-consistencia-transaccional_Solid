# Banking U2 — Consistencia transaccional

Aplicacion web .NET 10 / ASP.NET Core, Npgsql 10.0.3 y PostgreSQL 18.
Estudiante: Rommel Sahid Bosquez Felix. Dominio asignado: Banking System.
Incluye Docker Compose con aplicacion, PostgreSQL y proveedor externo simulado.
No requiere instalar .NET ni PostgreSQL en la otra PC si se ejecuta con Docker.

## Iniciar en Windows

Instalar Docker Desktop con contenedores Linux y **abrirlo**. Esperar a que el motor este listo.
La primera compilacion necesita Internet para descargar imagenes y NuGet.
En PowerShell, dentro de esta carpeta:

```powershell
cd 'C:\Users\Usuario\Desktop\Aplicación de consistencia transaccional_U2'
powershell -ExecutionPolicy Bypass -File .\scripts\Iniciar.ps1
docker compose ps
docker compose logs -f app
```

Abrir **http://localhost:5080**. Ctrl+C solo sale de la vista de logs.
`Iniciar.ps1` prepara el .env y construye las imagenes desde una carpeta temporal
sin tildes para evitar un fallo gRPC de Docker BuildKit en Windows con la ruta
`Aplicación`. El codigo queda en la carpeta original. Finalmente ejecuta
`docker compose up -d --no-build`. Para reconstruir tras editar, repetir Iniciar.ps1.
En una ruta sin tildes tambien puede usarse Preparar.ps1 y `docker compose up --build -d`.
La aplicacion no requiere login: el campo Operador identifica al actor de demostracion
y el Proxy valida su presencia; no representa autenticacion de produccion.
El proveedor simulado solo autoriza, no realiza pagos ni efectos monetarios externos.

El script Preparar genera `.env` con contrasena aleatoria y puertos.
Variables: POSTGRES_PASSWORD (obligatoria), POSTGRES_PORT (55432), APP_PORT (5080).
No subir `.env` al repositorio ni incluirlo en el ZIP. Cada PC puede generar el suyo.
Si algun puerto esta ocupado, cambiarlo en `.env` y ejecutar de nuevo Compose.
Los datos SQL se cargan automaticamente solo al crear un volumen vacio.
No ejecutar el SQL MySQL de `original/` en PostgreSQL.

## Pantallas

- Resumen: conteos, ultimos movimientos y prestamos con pagos relacionados.
- Clientes: listar, buscar, crear, editar y eliminar logicamente.
- Cuentas: relaciones, estado y saldo; se dispone de 100 cuentas iniciales.
- Transferencias: seleccionar cuentas activas, monto y error simulado opcional.
- Laboratorio: prueba concurrente, control del proveedor e historial Circuit Breaker.

## Entrar a Docker y consultar las tablas

```powershell
# Consola SQL directamente dentro del contenedor
docker compose exec postgres psql -U banking -d banking_system
```

Dentro de psql:

```sql
\dt
SELECT * FROM customer LIMIT 10;
SELECT account_id, account_number, balance, status FROM account ORDER BY account_id;
SELECT * FROM bank_transaction ORDER BY transaction_id DESC LIMIT 10;
SELECT * FROM loan LIMIT 10;
SELECT * FROM loan_payment LIMIT 10;
SELECT * FROM branch;
\q
```

Una sola consulta desde PowerShell:

```powershell
docker compose exec postgres psql -U banking -d banking_system -c 'SELECT COUNT(*) FROM customer;'
# Consola Linux del contenedor PostgreSQL
docker compose exec postgres sh
# salir con: exit
```

Para pgAdmin 4: registrar servidor, host `localhost`, puerto `55432`,
maintenance database `banking_system`, usuario `banking`, contrasena de tu `.env`.
Desde pgAdmin instalado en la PC se usa localhost; desde app en Docker se usa postgres.

## Pruebas del taller

1. CRUD: crear, editar y eliminar un cliente. El borrado logico conserva sus relaciones.
2. COMMIT: elegir dos cuentas Active con saldo suficiente y transferir 10.
   Se actualizan ambos saldos y aparece un movimiento; se muestran los dos observadores.
3. ROLLBACK: anotar ambos saldos y el numero de movimientos, marcar error simulado
   y transferir. El error ocurre **despues** del debito; los dos saldos y el historial
   permanecen iguales. El saldo sigue protegido por CHECK y claves foraneas.
4. Concurrencia: proveedor disponible y circuito cerrado; pulsar Ejecutar dos operaciones.
   Crea un cliente y tres cuentas nuevas para cada prueba (100, 0, 0).
   Dos solicitudes transfieren 80 desde el mismo origen; debe haber un COMMIT y
   un rechazo por saldo insuficiente. Saldo origen 20 y suma de saldos 100.
   Se usan conexiones independientes, READ COMMITTED y SELECT FOR UPDATE en orden de ID.
5. Durabilidad: anotar el ID del ultimo movimiento confirmado. Ejecutar:

```powershell
docker compose restart app postgres
docker compose ps
docker compose exec postgres psql -U banking -d banking_system -c 'SELECT * FROM bank_transaction ORDER BY transaction_id DESC LIMIT 5;'
```

Esperar que PostgreSQL este disponible, actualizar la web y comprobar el ID y los saldos.
El volumen `banking_data` conserva los datos con restart y con `docker compose down`.
**No usar `docker compose down -v`: elimina el volumen con los datos.**

6. Proxy: enviar una transferencia y mirar logs de app, que muestran operador, fecha y cuenta.
   Una llamada API sin cabecera X-Operator es rechazada antes de ejecutar el servicio real.
7. Observer: despues de COMMIT, AuditObserver y NotificationObserver reaccionan.
   Se ven en logs y en el resultado web. Son notificaciones de demostracion en memoria/logs;
   un fallo posterior al COMMIT no debe presentarse como una transferencia revertida.
8. Adapter: logs ADAPTER muestran conversion de Amount a value y cuentas a code;
   el proveedor expone POST /executeTransaction con una interfaz distinta.
9. Circuit Breaker: pulsar Provocar fallo; intentar tres transferencias validas.
   El circuito pasa a OPEN; la cuarta no aumenta `provider.calls`.
   Esperar 10 segundos dejando el proveedor fallando y enviar: HALF-OPEN -> OPEN.
   Recuperar servicio, esperar 10 segundos y enviar: HALF-OPEN -> CLOSED.
   HALF-OPEN es transitorio; su evidencia queda en el historial y logs.
   El estado es compartido por solicitudes dentro de una instancia app; reiniciar app
   reinicia el circuito. Los saldos persistentes no se reinician.

Prueba automatizada (deja datos de demostracion):

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\Probar.ps1
```

Para otro puerto: agregar `-BaseUrl http://localhost:PUERTO`.
El script verifica CRUD, COMMIT, ROLLBACK, concurrencia, Proxy, dos observadores,
Adapter y transiciones OPEN/HALF-OPEN/CLOSED, incluida recuperacion fallida.
La durabilidad se verifica con el reinicio descrito arriba.

## Arquitectura y comentarios en el codigo

Cliente web -> endpoints ASP.NET Core -> ITransferService / TransferProxy -> TransferService
-> ITransferAuthorizer / ProviderAdapter / CircuitBreaker -> proveedor externo simulado.
Tras autorizar: ITransferRepository / BankRepository -> transaccion PostgreSQL.
Tras COMMIT: ITransferObserver -> AuditObserver + NotificationObserver.

| Concepto | Ubicacion y aplicacion |
|---|---|
| S | Data/BankRepository persiste; Services/Transfers coordina; web presenta |
| O | Coleccion de observadores extensible por inyeccion sin modificar TransferService |
| L | AuditObserver y NotificationObserver cumplen el mismo contrato intercambiable |
| I | Contratos separados de clientes, consultas, transferencias, autorizacion y observadores |
| D | TransferService depende de ITransferRepository, ITransferAuthorizer e ITransferObserver |
| Proxy | Services/Transfers.cs, mismo contrato ITransferService que servicio real |
| Observer | Publicacion posterior a COMMIT, dos reacciones separadas |
| Adapter | Services/ExternalIntegration.cs, transforma interfaz al formato externo |
| Circuit Breaker | Services/ExternalIntegration.cs, 3 fallos y espera de 10 segundos |
| Atomicidad | Data/BankRepository.cs, transaccion, error tras debito, rollback explicito |
| Consistencia | db/01_banking.sql, CHECK, NOT NULL, UNIQUE y claves foraneas |
| Aislamiento | FOR UPDATE ordenado; se comprueba saldo despues de adquirir bloqueo |
| Durabilidad | CommitAsync y volumen Compose para PostgreSQL |

Los archivos incluyen comentarios `================ Aqui se aplico ... ================`.
Los logs de auditoria y notificacion son evidencia de Observer, no un registro bancario
persistente adicional: el historial monetario persistente es bank_transaction.

## Base y adaptaciones

Original suministrado: banking-db-practice-main/sql, repositorio de referencia
https://github.com/nishanaryal/banking-db-practice (copia original en original/).
Se conservan seis tablas, datos de muestra, indices y cuatro vistas.
Tablas: customer, account, bank_transaction, loan, loan_payment, branch.
El flujo principal usa clientes, cuentas y movimientos; prestamos/pagos se visualizan relacionados.
Se conservaron tablas auxiliares necesarias para no romper claves foraneas del original.

Cambios: UPDATE JOIN de MySQL -> UPDATE con subconsultas correlacionadas;
DATETIME -> TIMESTAMP; ENUM -> VARCHAR con CHECK;
AUTO_INCREMENT -> IDENTITY; retiro de USE, ENGINE, FOREIGN_KEY_CHECKS y CREATE DATABASE MySQL;
sincronizacion de secuencias despues de IDs explicitos; identidades para customer/account;
is_deleted para clientes; restricciones de nombres no vacios.
sp_transfer se adapto a PL/pgSQL sin COMMIT interno y con orden estable de bloqueos.
La aplicacion implementa la transaccion en C# para demostrar manejo transaccional.
No se migraron los otros procedimientos MySQL ni las respuestas de ejercicios,
que no son necesarios para el flujo del programa.

Referencias tecnicas: https://www.npgsql.org/doc/basic-usage.html
y https://www.nuget.org/packages/Npgsql/10.0.3 .

## Backup, restauracion y traslado a otra PC

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Respaldar.ps1
# Restaurar en OTRA base nueva para verificar sin borrar la actual
powershell -ExecutionPolicy Bypass -File .\scripts\Restaurar.ps1
docker compose exec postgres psql -U banking -d banking_restored -c '\dt'
```

`Respaldar.ps1` usa pg_dump -F c dentro del contenedor y copia el binario a
`backups/BD_BosquezFelixRommelSahid.backup` sin redireccion de PowerShell.
Incluye estructura, relaciones, restricciones y datos presentes al respaldar.
`Restaurar.ps1` crea una base nueva; si el nombre ya existe se detiene para no sobrescribirla.
Para elegir otro archivo o nombre: `-File backups/archivo.backup -Database banking_otra`.

Mover el codigo y el backup a la otra PC, instalar/abrir Docker Desktop, generar su .env,
ejecutar Compose y restaurar el backup. Los volumenes NO se trasladan al copiar la carpeta.
Para usar la base restaurada en la app, cambiar en docker-compose.yml
`ConnectionStrings__Banking: ... Database=banking_restored; ...` y ejecutar
`docker compose up -d app`. En pgAdmin seleccionar esa misma base.
No modificar POSTGRES_DB de un volumen existente esperando que lo vuelva a inicializar.

## Ejecucion local opcional y estructura

Con SDK .NET 10 y PostgreSQL configurado, cargar db/01_banking.sql en una base vacia.
Definir ConnectionStrings__Banking y ExternalServiceUrl como variables de entorno.
Ejecutar ExternalService en un puerto y BankingApp en otro con dotnet run --project.
Para Docker no se requiere este procedimiento ni scaffolding EF/Identity.
El ejemplo PrimeVideo se uso como referencia de comandos; el dominio implementado es bancario.

Para detener conservando datos: `docker compose down`.
Para volver a iniciar: `docker compose up -d`.
Para consultar errores: `docker compose logs --tail 100 app postgres external-service`.

Codigo en src/, base en db/, utilidades en scripts/, pruebas en tests/.
Antes de entregar un ZIP, excluir .env, .git, bin, obj, .vs, logs y archivos temporales.
Conservar csproj, packages.lock.json, SQL, Dockerfile, Compose, scripts y backup.
Este proyecto entrega el programa y las instrucciones; no genera el informe PDF.

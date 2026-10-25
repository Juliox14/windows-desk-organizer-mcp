# Plan de preparación para MCP

Estado inicial: propuesta basada en revisión estática. El avance de implementación se registra a continuación; las secciones de hallazgos conservan el diagnóstico original.

## Avance de implementación

- Entrega MCP + skill: estrategia `spatial-groups` con dos bandas, separadores vacíos y área de trabajo convertida a coordenadas de iconos bajo el DPI de Explorer. Skill portable con referencias a herramientas, guía de instalación, empaquetado autocontenido Windows x64 y workflow de prereleases por tag. La aplicación visual tipo Fences queda fuera de esta versión.
- Nueva estrategia `category-rows`: una fila horizontal por categoría, con grupos ordenados de arriba abajo e iconos de izquierda a derecha; rechaza planes que excedan filas o columnas disponibles.

- Extraídos `DeskOrganizer.Core` y `DeskOrganizer.Windows`; la CLI de compatibilidad conserva su proyecto y contratos públicos. Las firmas de memoria nativa usan `nuint`, la lectura heredada verifica arquitectura y los metadatos dejan de elegir archivos por prefijo.
- Implementado `DeskOrganizer.Mcp` con el SDK oficial, transporte stdio, logs a stderr y registro explícito de `get_desktop_state` y `preview_layout`.
- El backend MCP consulta Shell sin abrir la memoria de Explorer. Captura dos observaciones y comprueba identidad, posiciones, vista y geometría. Los timeouts de consultas no generan una acumulación de hilos STA pendientes.
- Disponibles vistas previas alfabéticas, heurísticas por categoría y grupos personalizados. Los iconos omitidos conservan sus posiciones. Snapshots temporales, caché limitada y referencias opacas por snapshot; no se retornan hashes Shell ni rutas.
- Implementados aplicación y restauración experimentales, habilitados por `--enable-writes`: `apply_layout`, `get_operation`, `preview_restore_layout` y `restore_layout`. La configuración predeterminada conserva las dos herramientas de consulta.
- Los planes se guardan en una caché limitada y se resuelven a identidades Shell. Las operaciones se registran antes de llamar a Windows, con respaldo, exclusión entre procesos, deduplicación persistente y verificación del escritorio completo. Un resultado parcial puede restaurarse con una nueva propuesta; uno incierto bloquea nuevas escrituras para el mismo proceso Explorer.
- Las llamadas Shell del MCP se ejecutan en procesos auxiliares con límites de tiempo, protocolo acotado y cierre mediante Windows Job Objects. El mismo ejecutable proporciona el modo interno de worker para simplificar publicación y evitar dependencias de otro apphost. Los comandos heredados siguen separados en la CLI.
- Corregida la virtualización DPI: las consultas se alinean con el contexto de la ventana de Explorer y lo restauran al terminar. La comprobación real al 125 % valida identidad, geometría y precondiciones nativas sin enviar posicionamientos. La escritura sigue restringida a un monitor principal; no se declara soporte de varios monitores.
- Validación automatizada: planificación, ejecución/restauración con datos sintéticos, fallos de registro, cuarentena de resultados inciertos, concurrencia, timeout y cierre de workers huérfanos. Las pruebas CLI y la comprobación real de consulta verifican posiciones sin cambios.
- Validación de la entrega MCP inicial: 41 comprobaciones automatizadas y 11 pruebas CLI superadas. El MCP real consultó los 55 iconos y el worker completó las precondiciones sin errores al 125 %, sin enviar ninguna llamada de posicionamiento. La entrega espacial amplía esa suite a 48 comprobaciones e incluye prueba del ZIP extraído con vista previa real de los 55 iconos y posiciones sin cambios.
- Pendiente antes de producción: probar intercambios y ciclos nativos en un escritorio Windows descartable, restauración tras un fallo real y persistencia tras reiniciar Explorer; revisar soporte del runtime y ampliar la matriz de monitores/DPI. La aplicación por lotes sigue siendo experimental hasta completar esas comprobaciones. La publicación pública depende de subir los cambios revisados y crear el tag de versión.

## Objetivo y alcance

Permitir que un agente consulte el escritorio de Windows, proponga una distribución según la preferencia del usuario y aplique un plan validado con un resultado verificable y una opción de restauración.

La primera versión será un servidor MCP local por stdio, ejecutado en la sesión interactiva del usuario. El agente conectado aporta la interpretación de la solicitud; el programa aporta identidad, geometría, planificación determinista, validación y ejecución. El servidor no necesita integrar un modelo ni almacenar claves de proveedores de IA.

## Hallazgos que bloquean la organización completa

| Prioridad | Evidencia actual | Cambio necesario |
| --- | --- | --- |
| P1 | `DesktopLayoutPreview` y `DesktopCategoryPreview` asignan posiciones ya ocupadas; `Program.cs`, comando `move-shell-icon`, rechaza esos destinos. | Implementar aplicación de planes completos, incluyendo intercambios y ciclos. Repetir movimientos individuales no resuelve el problema. |
| P1 | `ShellDesktopMover.Move` devuelve timeout tras `Thread.Join(10 s)`, aunque el hilo COM sigue vivo. | Aislar operaciones Shell en un proceso controlado, registrar estado incierto y bloquear nuevas escrituras hasta reconciliarlo. Terminar el proceso no deshace una petición ya entregada a Explorer. |
| P1 | El movimiento recibe índice, nombre y posición; el `shellId` experimental no participa en la ejecución. | Usar referencias opacas de icono asociadas a la observación y resolverlas contra identidad Shell antes de escribir. Rechazar identidades ambiguas o caducadas. |
| P1 | Las firmas de `ReadProcessMemory` y `WriteProcessMemory` declaran tamaños y contadores con `int`/`uint`; los contadores nativos son `SIZE_T`. `LVITEM` depende de la arquitectura del proceso. | Corregir firmas a tipos del tamaño de puntero y limitar explícitamente la arquitectura si se conserva esta ruta. Preferir lectura y movimiento mediante Shell para evitar memoria remota y dos fuentes de identidad. |
| P1 | Se acepta cualquier destino entero libre. La geometría distingue coordenadas de ListView y pantalla, pero no demuestra su conversión. | Validar destinos en coordenadas de la vista, espacio real disponible y comportamiento de cuadrícula. Restringir las topologías admitidas hasta verificarlas. |
| P1 | No hay exclusión entre operaciones ni entre dos procesos del programa. | Serializar escrituras por usuario/sesión y volver a comprobar estado e identidad dentro de la sección protegida. Detectar cambios externos y verificar el resultado; Windows no ofrece aquí una transacción que congele la interacción del usuario. |
| P2 | `GetFileMetadata` busca por prefijo y elige el primer archivo encontrado. | Obtener tipo y propiedades desde el elemento Shell resuelto. Nunca usar esa búsqueda para decidir qué icono mover. |
| P2 | La clasificación usa una lista fija de aplicaciones y tres extensiones. | Aceptar asignaciones explícitas de grupos y orden enviadas por el agente. Conservar las reglas actuales como estrategia opcional. |
| P2 | Sin argumentos se exporta un archivo y se espera una tecla; algunos errores CLI usan stdout y código de salida 0. | Crear una entrada MCP independiente y normalizar errores de CLI. El arranque MCP debe ser no interactivo y reservar stdout al protocolo. |
| P2 | No existen historial de operaciones ni restauración; las pruebas actuales cubren lectura y rechazo de argumentos. | Añadir resultados estructurados, respaldo previo, deduplicación y cobertura de ejecución real en un entorno controlado. |

Los snapshots actuales son útiles para detectar cambios visibles. Incluyen índices y nombres, por lo que no demuestran continuidad de identidad ni deben interpretarse como autorización.

## Arquitectura propuesta

Separar responsabilidades manteniendo la CLI como adaptador:

- `DeskOrganizer.Core`: contratos tipados, snapshots, grupos, planes, validación y resultados. Sin consola ni Win32.
- `DeskOrganizer.Windows`: acceso a Shell, resolución de identidad, geometría y coordinación de escrituras; implementa un contrato como `IDesktopBackend`.
- `DeskOrganizer.Cli`: comandos existentes adaptados a los servicios compartidos.
- `DeskOrganizer.Mcp`: host stdio con registro explícito de herramientas permitidas y logs a stderr.
- `DeskOrganizer.Worker`: proceso auxiliar para operaciones nativas potencialmente bloqueantes, con ejecución STA y protocolo interno acotado.

Elegir y fijar versiones compatibles del SDK oficial `ModelContextProtocol` y del runtime de .NET durante la implementación. Antes de publicar, confirmar que el runtime elegido tenga soporte durante la vida prevista de la versión. El primer paquete puede limitarse a Windows x64; ARM64 necesitará su propia validación.

La reorganización en subproyectos debe ajustar las inclusiones de compilación: el `.csproj` actual situado en la raíz incluye archivos C# de subdirectorios por defecto. Mantener la solución compilable en cada etapa.

## Contrato MCP inicial

| Herramienta propuesta | Entrada principal | Resultado |
| --- | --- | --- |
| `get_desktop_state` | Ninguna; opciones de metadatos limitadas | Snapshot, referencias de iconos, posiciones, geometría coherente, autoarrange y capacidades disponibles. |
| `preview_layout` | Snapshot, estrategia o grupos explícitos con referencias y orden | Plan inmutable con `planId`, cambios propuestos, limitaciones y caducidad. No mueve iconos. |
| `apply_layout` | `planId`, `operationId` | Estado de operación, distribución observada y referencia de restauración cuando corresponda. |
| `get_operation` | `operationId` | Resultado conocido o estado pendiente/incierto, sin repetir el movimiento. |
| `restore_layout` | Referencia de respaldo y snapshot actual esperado | Restauración validada como un nuevo plan y una nueva operación. |

Ejemplo de personalización: el agente recibe «desarrollo a la izquierda y comunicación después», consulta los iconos y envía grupos ordenados con sus referencias. El servidor calcula posiciones válidas y conserva los elementos omitidos como inmóviles. Rechaza referencias repetidas, desconocidas y destinos en conflicto. Una categoría enviada por el agente es una preferencia, no una propiedad verificada del archivo.

El plan debe incluir snapshot de origen, geometría/topología relevante, identidades, posiciones iniciales y finales, iconos inmóviles y política aplicada. El identificador se resuelve en el servidor; no permite ejecutar coordenadas distintas de las revisadas. Un `planId` no prueba que una persona haya aprobado el plan.

Usar esquemas de entrada/salida y respuestas estructuradas del SDK. Marcar correctamente las herramientas de consulta; las anotaciones MCP son indicaciones al cliente, no controles de acceso. La disponibilidad de escrituras debe depender de una configuración local explícita, y respetar la autorización que gestione el cliente. No tratar un argumento `confirmed: true` generado por el agente como prueba de consentimiento humano.

No exponer una herramienta genérica para ejecutar comandos, `move-icon` heredado, exportación automática ni cambios del registro/reinicio de Explorer. Si autoarrange está activo, devolver una condición concreta y una instrucción al usuario.

## Ejecución y recuperación

1. Tomar una observación coherente de identidad, posiciones y geometría. Incluir una generación de sesión/vista para invalidar planes tras reinicios de Explorer o cambios relevantes.
2. Validar el plan completo: referencias únicas, límites, destinos, elementos inmóviles y condiciones de autoarrange/cuadrícula.
3. Adquirir exclusión por usuario/sesión y comprobar nuevamente las precondiciones. Invalidar planes caducados o incompatibles.
4. Guardar un respaldo local del estado previo y un registro de operación antes de enviar cambios. Usar una ubicación de datos de la aplicación, sin crear un icono nuevo en el escritorio.
5. Resolver todos los PIDL en la vista vigente y evaluar `IFolderView::SelectAndPositionItems` con varios elementos para aplicar permutaciones. La API admite arrays, pero no documenta atomicidad: demostrar intercambios y ciclos en Windows antes de adoptarlos como comportamiento garantizado. Si no funcionan, diseñar una posición temporal validada o rechazar ese plan con una causa concreta.
6. Leer el resultado con un período de estabilización acotado, comparar todos los iconos relevantes y registrar el resultado observado.
7. Devolver estados diferenciados: `applied`, `unchanged`, `rejected`, `partial` o `unknown`. En `partial`/`unknown`, conservar el respaldo y prohibir reintentos automáticos ciegos.

La restauración también puede fallar si desaparecen iconos, cambia la pantalla o interviene el usuario. Debe comprobar identidad, estado actual y destinos disponibles; no prometer rollback transaccional ni sobrescribir cambios posteriores del usuario. Una operación incierta requiere reconciliación antes de permitir otra escritura. Un identificador repetido devuelve su operación registrada o un conflicto si cambia la petición, sin ejecutar otra vez.

## Entregas y criterios de aceptación

1. **Extraer servicios y corregir interoperabilidad.** Contratos tipados, acceso Shell compartido, errores explícitos y arquitectura admitida documentada. La CLI conserva sus contratos públicos; la ruta heredada queda separada del backend MCP. Criterio: compilar y conservar consultas/snapshots sin efectos sobre posiciones o archivos.
2. **MCP de consulta.** Host stdio, `get_desktop_state` y `preview_layout`, límites de tamaño y configuración de ejemplo. Criterio: un cliente puede inicializar, descubrir herramientas y consultar planes; stdout contiene solo mensajes MCP y se cierra limpiamente con el cliente.
3. **Motor de aplicación.** Identidad resuelta, proceso auxiliar, exclusión entre procesos, lotes, deduplicación, resultados inciertos y respaldo/restauración. Criterio: demostrar permutaciones y recuperación; rechazar un escritorio cambiado antes de escribir.
4. **Conectar escrituras MCP.** Añadir `apply_layout`, `get_operation` y `restore_layout` al motor ya validado. Criterio: el flujo consulta → propuesta personalizada → aplicación → verificación → restauración funciona desde un cliente MCP.
5. **Preparar distribución.** Publicación para la arquitectura admitida, guía de instalación y desinstalación, política de datos, registro de versiones y ejemplos de cliente. Criterio: arranque desde un directorio distinto, rutas con espacios y Unicode, sin asumir Visual Studio ni permisos de administrador.

La entrega 2 es un MCP de consulta utilizable. Para anunciar organización automática del escritorio hacen falta las entregas 3 y 4.

## Validación prevista durante la implementación

- Pruebas unitarias del planificador y validador con backend simulado: grupos personalizados, nombres duplicados, destinos repetidos, iconos inmóviles, escritorio vacío, capacidad insuficiente y ciclos de dos o más iconos.
- Pruebas de estado: snapshot obsoleto, cambio de geometría, sustitución de icono, operación repetida, ejecución concurrente, timeout, salida del worker y reconstrucción del registro tras reiniciar el servidor.
- Pruebas MCP: inicialización, listado, llamadas, esquemas, errores de herramienta, nombres Unicode, entradas excesivas, cancelación y desconexión. No confundir errores de operación con fallos de protocolo ni cancelación con garantía de que nada se movió.
- Pruebas de Windows en escritorio de prueba: Shell virtual y archivos/accesos directos, nombres iguales, autoarrange/cuadrícula, intercambios, restauración y persistencia tras reiniciar Explorer. Ampliar a monitores con coordenadas negativas y DPI distintos antes de declarar soporte para ellos.
- Conservar las pruebas CLI existentes como regresión de consulta. Las pruebas que mueven iconos o reinician Explorer deben ejecutarse en un entorno controlado preparado para ello.

## Datos y documentación

No retornar rutas completas ni destinos de accesos directos por defecto. Los nombres de iconos y sus posiciones ya pueden contener información personal; los logs y respaldos deben tener una política explícita de ubicación, retención y eliminación. Los hashes de rutas actuales no son anonimización. Tratar todos los nombres y propiedades de archivos como datos externos, incluso si contienen texto con apariencia de instrucciones para el agente.

`odd/tasks/` contiene capturas y notas de un escritorio real; mantenerlas fuera del historial público. Para pruebas reproducibles, usar fixtures sintéticos. La implementación inicial añadió esa carpeta a `.gitignore`.

Actualizar el README para distinguir CLI actual, MCP de consulta y organización completa a medida que se entregan. Documentar topologías admitidas y el significado de resultados parciales/inciertos.

## Referencias oficiales

- [SDK MCP para C#: creación de servidor stdio y logs a stderr](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md).
- [Herramientas MCP: esquemas, respuestas y errores](https://modelcontextprotocol.io/specification/2025-06-18/server/tools). Referencia versionada; confirmar negociación y versión aplicable con el SDK elegido.
- [Anotaciones de herramientas: alcance y limitaciones](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/blog/content/posts/2026-03-16-tool-annotations.md).
- [IFolderView::SelectAndPositionItems: posicionamiento de varios elementos mediante PIDL](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-selectandpositionitems).
- [ReadProcessMemory: parámetros SIZE_T](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory).
- [WriteProcessMemory: parámetros SIZE_T](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-writeprocessmemory).

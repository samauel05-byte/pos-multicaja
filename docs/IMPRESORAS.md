# Impresoras térmicas

**Estado: SIN VALIDAR con hardware real.** En el entorno de desarrollo no hubo impresora térmica. Lo verificado es que el servidor genera el ticket correctamente (texto de 32/42/48 columnas y una secuencia ESC/POS que empieza con `ESC @` y termina con corte parcial `GS V 66 0`, comprobado por prueba automática). Que una impresora concreta lo imprima bien **no está probado**.

## Principios (implementados)
1. La venta se confirma y guarda **antes** de imprimir. Imprimir nunca modifica importes, inventario ni caja.
2. Cada ticket pedido crea un `PrintJob` (pendiente → impreso/falló). Reimprimir crea otro trabajo, marca “COPIA / REIMPRESION”, incrementa el contador y queda en auditoría. **No duplica la venta.**
3. Si la impresión falla, la interfaz avisa y ofrece “Reimprimir”; la venta sigue registrada.
4. Los tickets se identifican como **“DOCUMENTO NO FISCAL”** mientras el módulo fiscal esté pendiente.

## Método A — Impresora instalada en Windows (el implementado)
La impresora térmica (Epson TM-T20/T88, Bixolon, Star, genérica 58/80 mm) se instala con su controlador y se define como **predeterminada de la caja**. El navegador abre una página de impresión de ancho 58/80 mm (`@page size`) con el texto del servidor.
- Para imprimir sin diálogo: Chrome/Edge con `--kiosk-printing` (acceso directo: `chrome.exe --kiosk-printing https://pos-servidor:5443`).
- Limitación: el navegador **no informa** si la impresora imprimió; la interfaz dice “enviado a impresión”, no “impreso”.
- Corte de papel y apertura de gaveta dependen del controlador/configuración del driver (“cortar al final del documento”, “abrir gaveta”).

## Método B — ESC/POS directo (preparado, no implementado)
La API ya entrega `escPosBase64` en `POST /api/sales/{id}/ticket`. Un pequeño **agente local** en cada caja (servicio .NET/Node en `127.0.0.1`) podría recibir esos bytes y enviarlos por: USB (WinUSB/`RAW` vía spooler), red (TCP 9100) o serie. Pendiente de construir y de validar impresora por impresora. Notas: página de códigos CP858 ya seleccionada (`ESC t 19`); verificar acentos y “ñ” en cada modelo.

## Lista de validación con hardware (marcar antes de declarar la función terminada)
- [ ] Imprime el ticket de una venta de 1 y de 30 líneas sin cortar columnas (58 mm = 32 col., 80 mm = 42/48 col.)
- [ ] Acentos y “ñ” correctos
- [ ] Corte automático al final
- [ ] Reimpresión sale con “COPIA”
- [ ] Impresora apagada / sin papel: la venta queda guardada y se puede reimprimir después
- [ ] Apertura de gaveta con venta en efectivo (si aplica)
- [ ] Dos cajas imprimiendo a la vez en impresoras distintas
- [ ] Reinicio de la PC de caja: la impresora sigue siendo la predeterminada

import type { ReactNode } from 'react'
import { Link } from 'react-router'

/**
 * Datos de quien responde por la plataforma. Los textos legales los leen personas ajenas al panel (por ejemplo, Meta al revisar
 * la app), así que estas páginas son públicas y no usan sesión ni llamadas a la API.
 */
const LEGAL = {
  // Pendiente de confirmar con Pixel DC: razón social y correo de contacto para privacidad.
  product: 'Pixela',
  company: 'Pixel DC [razón social pendiente]',
  email: '[correo de contacto pendiente]',
  place: 'Colombia',
  updated: '24 de noviembre de 2026',
}

function LegalPage({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="mx-auto max-w-3xl space-y-6 px-5 py-10 text-sm leading-relaxed">
      <header className="space-y-1">
        <p className="text-xs text-muted-foreground">
          {LEGAL.product} · {LEGAL.company}
        </p>
        <h1 className="text-2xl font-semibold">{title}</h1>
        <p className="text-xs text-muted-foreground">Última actualización: {LEGAL.updated}</p>
      </header>
      {children}
      <nav className="flex flex-wrap gap-4 border-t pt-4 text-xs text-muted-foreground">
        <Link to="/privacidad">Política de privacidad</Link>
        <Link to="/terminos">Condiciones del servicio</Link>
        <Link to="/eliminacion-de-datos">Eliminación de datos</Link>
      </nav>
    </main>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-2">
      <h2 className="text-base font-medium">{title}</h2>
      {children}
    </section>
  )
}

export function PrivacyPage() {
  return (
    <LegalPage title="Política de privacidad">
      <p>
        {LEGAL.product} es una plataforma de {LEGAL.company} que permite a empresas enviar correos transaccionales y atender a sus clientes con chatbots en su sitio web, WhatsApp, Messenger e Instagram. Esta política explica qué datos
        se tratan, para qué y cómo puedes ejercer tus derechos, conforme a la Ley 1581 de 2012 de Colombia y sus normas complementarias.
      </p>

      <Section title="Quién es quién">
        <p>
          Cuando escribes a un chatbot, la empresa a la que le escribes (el negocio que contrató {LEGAL.product}) decide para qué usa la conversación: es la responsable del tratamiento. {LEGAL.company} actúa como encargada: aloja el
          servicio y procesa los mensajes siguiendo las instrucciones de esa empresa. Para los usuarios del panel y para los datos de facturación y soporte de la plataforma, {LEGAL.company} es la responsable.
        </p>
      </Section>

      <Section title="Qué datos tratamos">
        <ul className="list-disc space-y-1 pl-5">
          <li>
            <strong>Quien escribe a un chatbot:</strong> el texto de los mensajes y las respuestas, la fecha y hora, y un identificador del contacto que entrega el canal (en WhatsApp, el número de teléfono; en Messenger e Instagram, un
            identificador asignado por Meta y el nombre de perfil que Meta comparta). En el chat web, un identificador anónimo de la sesión. Si la persona decide dejar sus datos para que la contacten, el nombre, el correo, el teléfono y su
            interés, con su consentimiento.
          </li>
          <li>
            <strong>Usuarios del panel:</strong> nombre, correo, rol y registros de acceso necesarios para la seguridad.
          </li>
          <li>
            <strong>Destinatarios de correos:</strong> la dirección de correo, el estado de entrega, y las bajas o rebotes.
          </li>
        </ul>
        <p>No pedimos ni procesamos de forma deliberada datos sensibles. Te pedimos que no los escribas en el chat.</p>
      </Section>

      <Section title="Para qué los usamos">
        <ul className="list-disc space-y-1 pl-5">
          <li>Responder tus mensajes con información que la empresa cargó en su base de conocimiento.</li>
          <li>Pasar tus datos a la empresa cuando tú lo pides, para que te contacte.</li>
          <li>Medir el consumo del servicio, aplicar los límites acordados y prevenir abusos.</li>
          <li>Mantener la seguridad y el funcionamiento de la plataforma.</li>
        </ul>
        <p>No vendemos datos ni los usamos para publicidad ni para entrenar modelos propios.</p>
      </Section>

      <Section title="Con quién los compartimos">
        <ul className="list-disc space-y-1 pl-5">
          <li>
            <strong>Meta Platforms</strong> (WhatsApp, Messenger e Instagram) transporta los mensajes de esos canales. Meta trata los datos bajo sus propias condiciones y política de privacidad.
          </li>
          <li>
            <strong>Proveedores de infraestructura</strong> donde se aloja la plataforma.
          </li>
          <li>
            <strong>Un proveedor de inteligencia artificial</strong>, cuando la empresa lo activa, al que se envía el texto de la conversación y los fragmentos de su base de conocimiento para redactar la respuesta. No se le envían tu
            nombre, tu correo ni tu teléfono.
          </li>
          <li>La empresa con la que hablas, que ve las conversaciones en su panel.</li>
          <li>Autoridades, cuando la ley lo exija.</li>
        </ul>
      </Section>

      <Section title="Cuánto tiempo los conservamos">
        <p>
          Las conversaciones se conservan el tiempo que configure la empresa, que por defecto es de 90 días, y luego se eliminan. El texto recibido por los canales de Meta se borra de la cola interna al terminar de procesarse, o a más tardar
          a los pocos días. Se conservan, sin contenido de mensajes, los totales de consumo mensual necesarios para la facturación.
        </p>
      </Section>

      <Section title="Seguridad">
        <p>
          La comunicación va cifrada, los tokens de acceso a los canales se guardan cifrados, cada empresa solo puede ver sus propios datos y los accesos al panel están protegidos. Ningún sistema es infalible: si ocurre un incidente que te afecte, lo
          comunicaremos a las empresas responsables y a las autoridades según la ley.
        </p>
      </Section>

      <Section title="Tus derechos">
        <p>
          Puedes conocer, actualizar, rectificar y pedir la supresión de tus datos, y revocar tu autorización. Escribe a <a className="underline" href={`mailto:${LEGAL.email}`}>{LEGAL.email}</a> indicando el canal por el que
          escribiste y el dato que nos permita ubicarte (por ejemplo, el número de WhatsApp). Si la solicitud corresponde a la empresa responsable, se la haremos llegar. Mira también{' '}
          <Link className="underline" to="/eliminacion-de-datos">cómo pedir la eliminación de tus datos</Link>.
        </p>
      </Section>

      <Section title="Cambios y contacto">
        <p>
          Publicaremos aquí cualquier cambio con su fecha. Contacto: <a className="underline" href={`mailto:${LEGAL.email}`}>{LEGAL.email}</a>, {LEGAL.company}, {LEGAL.place}.
        </p>
      </Section>
    </LegalPage>
  )
}

export function TermsPage() {
  return (
    <LegalPage title="Condiciones del servicio">
      <p>
        Estas condiciones regulan el uso de {LEGAL.product}, la plataforma de {LEGAL.company} para el envío de correos y la atención con chatbots por web, WhatsApp, Messenger e Instagram. Al usar el servicio aceptas lo que sigue.
      </p>

      <Section title="El servicio">
        <p>
          Ofrecemos el servicio tal como está descrito en lo contratado, con los límites acordados. Las respuestas de un chatbot salen de la información que la empresa cargó y, si lo activa, de un modelo de inteligencia artificial: pueden
          contener errores y no sustituyen asesoría profesional.
        </p>
      </Section>

      <Section title="Responsabilidades de la empresa cliente">
        <ul className="list-disc space-y-1 pl-5">
          <li>Usar el servicio solo con contactos que le hayan escrito o que hayan dado su autorización, y cumplir las normas de protección de datos.</li>
          <li>Informar a sus clientes de que hablan con un asistente automático y publicar su propia política de privacidad.</li>
          <li>Cumplir las condiciones de Meta y de WhatsApp Business para los canales que conecte, incluidas sus reglas de mensajería.</li>
          <li>No usar el servicio para enviar contenido ilegal, engañoso, de acoso o correo no solicitado.</li>
          <li>Custodiar sus credenciales y los tokens de los canales.</li>
        </ul>
      </Section>

      <Section title="Límites y suspensión">
        <p>
          Podemos limitar o suspender una cuenta que supere lo acordado, ponga en riesgo la plataforma o incumpla estas condiciones. Cuando sea posible avisaremos antes.
        </p>
      </Section>

      <Section title="Responsabilidad">
        <p>
          Hacemos esfuerzos razonables para que el servicio esté disponible, pero no garantizamos que sea ininterrumpido ni que los canales de terceros, como los de Meta, estén siempre operativos. En lo que la ley permita, nuestra responsabilidad
          se limita al valor pagado por el servicio en el mes en que ocurrió el hecho.
        </p>
      </Section>

      <Section title="Datos personales">
        <p>
          El tratamiento de datos personales se rige por la <Link className="underline" to="/privacidad">política de privacidad</Link>.
        </p>
      </Section>

      <Section title="Cambios, ley aplicable y contacto">
        <p>
          Podemos actualizar estas condiciones y publicaremos la nueva versión aquí. Se rigen por las leyes de {LEGAL.place}. Contacto: <a className="underline" href={`mailto:${LEGAL.email}`}>{LEGAL.email}</a>.
        </p>
      </Section>
    </LegalPage>
  )
}

export function DataDeletionPage() {
  return (
    <LegalPage title="Eliminación de datos">
      <p>Si escribiste a un chatbot que usa {LEGAL.product} por la web, WhatsApp, Messenger o Instagram y quieres que borremos tus datos, sigue estos pasos.</p>

      <Section title="Cómo pedirlo">
        <ol className="list-decimal space-y-1 pl-5">
          <li>
            Escribe a <a className="underline" href={`mailto:${LEGAL.email}`}>{LEGAL.email}</a> con el asunto «Eliminar mis datos».
          </li>
          <li>Indica el canal por el que hablaste (web, WhatsApp, Messenger o Instagram) y el negocio con el que hablabas, si lo recuerdas.</li>
          <li>Indica el dato que nos permita ubicarte: tu número de WhatsApp, o el nombre de tu perfil de Facebook o Instagram. No envíes contraseñas ni documentos.</li>
        </ol>
      </Section>

      <Section title="Qué pasa después">
        <p>
          Confirmaremos la recepción y, tras verificar que la solicitud es tuya, eliminaremos tus conversaciones y datos de contacto que tengamos en la plataforma, en un plazo máximo de 15 días hábiles. Si los datos están bajo el control de la
          empresa con la que hablaste, le haremos llegar tu solicitud para que ella la atienda.
        </p>
      </Section>

      <Section title="Lo que no podemos borrar">
        <p>
          Los mensajes que quedan en tu propia cuenta de WhatsApp, Facebook o Instagram, y lo que Meta guarde, dependen de Meta: puedes gestionarlo en la configuración de privacidad de esas aplicaciones. Sí conservamos, sin contenido de
          mensajes, los totales de consumo necesarios para la facturación.
        </p>
      </Section>
    </LegalPage>
  )
}

namespace Pixela.Api.Endpoints;

/// <summary>Código del widget. El cargador va en la web del cliente; la página del chat corre en un iframe de esta API (mismo origen que /chat/v1).</summary>
internal static class WidgetAssets
{
    /// <summary>Cargador de una línea: pone el botón flotante y abre el chat en un iframe (sin cookies ni acceso a la página anfitriona).</summary>
    public const string Loader = """
        (function () {
          var script = document.currentScript;
          if (!script || window.__fcChatLoaded) return;
          var key = script.getAttribute('data-bot');
          if (!key) return;
          window.__fcChatLoaded = true;
          var base = new URL(script.src).origin;
          var color = /^#[0-9a-fA-F]{6}$/.test(script.getAttribute('data-color') || '') ? script.getAttribute('data-color') : '#2563eb';

          function mount() {
            var frame = document.createElement('iframe');
            frame.title = 'Chat';
            frame.setAttribute('loading', 'lazy');
            frame.style.cssText = 'position:fixed;right:20px;bottom:88px;width:360px;max-width:calc(100vw - 24px);height:560px;max-height:calc(100vh - 110px);border:0;border-radius:14px;box-shadow:0 8px 32px rgba(0,0,0,.25);background:#fff;z-index:2147483646;display:none';

            var button = document.createElement('button');
            button.type = 'button';
            button.setAttribute('aria-label', 'Abrir chat');
            button.style.cssText = 'position:fixed;right:20px;bottom:20px;width:56px;height:56px;box-sizing:border-box;padding:0;margin:0;display:flex;align-items:center;justify-content:center;line-height:0;border:0;border-radius:50%;cursor:pointer;box-shadow:0 4px 16px rgba(0,0,0,.3);z-index:2147483647;color:#fff;background:' + color;
            button.innerHTML = '<svg style="display:block;margin:0;flex:none" width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';

            var opened = false;
            button.addEventListener('click', function () {
              if (!opened) { frame.src = base + '/widget/' + encodeURIComponent(key); opened = true; }
              var show = frame.style.display === 'none';
              frame.style.display = show ? 'block' : 'none';
              button.setAttribute('aria-label', show ? 'Cerrar chat' : 'Abrir chat');
            });

            document.body.appendChild(frame);
            document.body.appendChild(button);

            // Sin data-color, el botón toma el color del chat (el del bot o el de la marca de la agencia). Si la consulta falla, queda el azul por defecto.
            if (!script.getAttribute('data-color')) {
              fetch(base + '/chat/v1/' + encodeURIComponent(key) + '/config')
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (c) { if (c && /^#[0-9a-fA-F]{6}$/.test(c.color || '')) button.style.background = c.color; })
                .catch(function () {});
            }
          }

          if (document.body) mount(); else document.addEventListener('DOMContentLoaded', mount);
        })();
        """;

    /// <summary>Aviso cuando el chat no puede mostrarse. Marcadores: __NONCE__ (CSP) y __MESSAGE__ (texto ya escapado).</summary>
    public const string Notice = """
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Chat</title>
        <style nonce="__NONCE__">
          html, body { height: 100%; margin: 0; font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; color: #374151; background: #fff; }
          body { display: flex; align-items: center; justify-content: center; padding: 24px; text-align: center; }
        </style></head>
        <body><p>__MESSAGE__</p></body></html>
        """;

    /// <summary>Página del chat. Marcadores: __KEY__ (clave del bot, ya escapada) y __NONCE__ (CSP). Todo lo que viene del servidor o del visitante se pinta con textContent.</summary>
    public const string Page = """
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Chat</title>
        <style nonce="__NONCE__">
          :root { --c: #2563eb; }
          * { box-sizing: border-box; }
          html, body { height: 100%; margin: 0; font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; color: #111827; background: #fff; }
          body { display: flex; flex-direction: column; }
          header { background: var(--c); color: #fff; padding: 12px 14px; font-weight: 600; display: flex; align-items: center; gap: 8px; }
          #logo { height: 24px; width: auto; max-width: 96px; border-radius: 4px; background: #fff; }
          [hidden] { display: none !important; }
          #log { flex: 1; overflow-y: auto; padding: 12px; display: flex; flex-direction: column; gap: 8px; }
          .m { max-width: 85%; padding: 8px 11px; border-radius: 12px; white-space: pre-wrap; overflow-wrap: anywhere; }
          .m.bot { background: #f3f4f6; align-self: flex-start; }
          .m.user { background: var(--c); color: #fff; align-self: flex-end; }
          .m.agent { background: #ecfdf5; border: 1px solid #a7f3d0; align-self: flex-start; }
          .m.agent .who { display: block; font-size: 11px; font-weight: 600; color: #047857; margin-bottom: 2px; }
          .m.agent a { color: var(--c); text-decoration: underline; }
          .m.bot a { color: var(--c); text-decoration: underline; }
          .thumb { display: block; width: 100%; max-width: 220px; height: 140px; object-fit: cover; border-radius: 8px; margin-top: 8px; border: 1px solid #d1d5db; background: #f3f4f6; }
          .thumbwrap { display: block; width: fit-content; max-width: 100%; }
          .mapbox { position: relative; margin-top: 6px; min-width: 220px; height: 110px; border-radius: 8px; overflow: hidden; border: 1px solid #d1d5db; }
          .mapbox iframe { width: 100%; height: 100%; border: 0; pointer-events: none; }
          .m.bot .mapcover { position: absolute; inset: 0; display: flex; align-items: flex-end; justify-content: flex-end; padding: 6px; text-decoration: none; color: #fff; font-size: 12px; }
          .m.bot .mapcover span { background: var(--c); padding: 3px 8px; border-radius: 999px; }
          .m.note { background: transparent; color: #6b7280; align-self: center; font-size: 12px; text-align: center; }
          form { display: flex; gap: 6px; padding: 10px; border-top: 1px solid #e5e7eb; }
          input[type=text], input[type=email], input[type=tel], textarea { width: 100%; padding: 8px 10px; border: 1px solid #d1d5db; border-radius: 8px; font: inherit; }
          button { font: inherit; border: 0; border-radius: 8px; padding: 8px 12px; background: var(--c); color: #fff; cursor: pointer; }
          button:disabled { opacity: .5; cursor: default; }
          button.link { background: none; color: var(--c); padding: 0; text-decoration: underline; }
          #lead { display: none; padding: 10px; border-top: 1px solid #e5e7eb; flex-direction: column; gap: 6px; max-height: 60%; overflow-y: auto; }
          #lead label.consent { display: flex; gap: 6px; font-size: 12px; align-items: flex-start; }
          .row { display: flex; gap: 6px; }
          #leadErr { color: #b91c1c; font-size: 12px; }
          #more { padding: 0 12px 8px; font-size: 12px; text-align: center; display: none; }
        </style>
        </head>
        <body data-key="__KEY__">
        <header><img id="logo" alt="" hidden><span id="title">Chat</span></header>
        <div id="log" role="log" aria-live="polite"><div class="m note" id="loading">Conectando…</div></div>
        <div id="more"><button type="button" class="link" id="openLead">Dejar mis datos de contacto</button></div>
        <section id="lead" aria-label="Datos de contacto">
          <input type="text" id="lName" placeholder="Tu nombre" maxlength="120" autocomplete="name">
          <input type="email" id="lEmail" placeholder="Correo" maxlength="254" autocomplete="email">
          <input type="tel" id="lPhone" placeholder="Teléfono (opcional)" maxlength="30" autocomplete="tel">
          <textarea id="lInterest" placeholder="¿En qué te podemos ayudar?" rows="2" maxlength="500"></textarea>
          <label class="consent"><input type="checkbox" id="lConsent"><span id="consentText">Autorizo el tratamiento de mis datos para que me contacten.</span></label>
          <div id="leadErr" role="alert"></div>
          <div class="row"><button type="button" id="leadSend">Enviar</button><button type="button" class="link" id="leadCancel">Cancelar</button></div>
        </section>
        <form id="form">
          <input type="text" id="input" placeholder="Escribe tu pregunta…" maxlength="1000" autocomplete="off" aria-label="Mensaje">
          <button type="submit" id="send">Enviar</button>
        </form>
        <script nonce="__NONCE__">
        (function () {
          var KEY = document.body.getAttribute('data-key');
          var token = null, busy = false, ended = false, privacyUrl = null, leadSent = false, texts = {};
          var withPerson = false, lastAgentAt = null, pollTimer = null, announcedPerson = false;
          var $ = function (id) { return document.getElementById(id); };
          var log = $('log'), input = $('input'), send = $('send');

          // Solo enlaces https: el texto se agrega siempre como nodo de texto (nunca como HTML) y el enlace se crea con href validado.
          function fillPlain(d, text) {
            var re = /https:\/\/[^\s<>"']+/g, last = 0, m;
            while ((m = re.exec(text)) !== null) {
              var url = m[0].replace(/[.,;:!?)\]]+$/, '');
              if (m.index > last) d.appendChild(document.createTextNode(text.slice(last, m.index)));
              var a = document.createElement('a');
              a.href = url; a.target = '_blank'; a.rel = 'noopener noreferrer'; a.textContent = url;
              d.appendChild(a);
              var map = /^https:\/\/www\.google\.com\/maps\/search\/\?api=1&query=(-?\d{1,3}(?:\.\d+)?),(-?\d{1,3}(?:\.\d+)?)$/.exec(url);
              if (map) {
                // Miniatura del mapa solo para enlaces de coordenadas de Google Maps (validadas como números). No es interactiva:
                // un enlace transparente encima la vuelve una tarjeta que abre Google Maps en otra pestaña.
                var box = document.createElement('div');
                box.className = 'mapbox';
                var f = document.createElement('iframe');
                f.title = 'Mapa'; f.loading = 'lazy'; f.referrerPolicy = 'no-referrer'; f.tabIndex = -1;
                f.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                f.src = 'https://www.google.com/maps?q=' + map[1] + ',' + map[2] + '&z=15&output=embed';
                var cover = document.createElement('a');
                cover.className = 'mapcover';
                cover.href = url; cover.target = '_blank'; cover.rel = 'noopener noreferrer';
                cover.setAttribute('aria-label', 'Abrir el mapa en Google Maps');
                var pill = document.createElement('span'); pill.textContent = '📍 Ver cómo llegar'; cover.appendChild(pill);
                box.appendChild(f); box.appendChild(cover);
                d.appendChild(box);
              }
              last = m.index + url.length;
              re.lastIndex = last;
            }
            if (last < text.length) d.appendChild(document.createTextNode(text.slice(last)));
          }

          // **texto** se dibuja en negrita con nodos <strong> (nunca como HTML); lo demás pasa por fillPlain (enlaces).
          function fill(d, text) {
            var re = /\*\*([^*\n]+?)\*\*/g, last = 0, m;
            while ((m = re.exec(text)) !== null) {
              if (m.index > last) fillPlain(d, text.slice(last, m.index));
              var b = document.createElement('strong');
              fillPlain(b, m[1]);
              d.appendChild(b);
              last = m.index + m[0].length;
            }
            if (last < text.length) fillPlain(d, text.slice(last));
          }

          function add(role, text) {
            var d = document.createElement('div');
            d.className = 'm ' + role;
            if (role === 'agent') {
              var who = document.createElement('span');
              who.className = 'who'; who.textContent = t('team', 'Equipo');
              d.appendChild(who);
              var line = document.createElement('div');
              fill(line, text);
              d.appendChild(line);
            } else if (role === 'bot') {
              // Las líneas "Foto: https://…" se dibujan como miniatura (no como texto); si el mensaje trae un enlace, la miniatura lo abre.
              var photos = [];
              var body = text.replace(/^[ \t]*Foto:[ \t]*(https:\/\/\S+)[ \t]*$/gim, function (all, u) { if (photos.length < 3) photos.push(u); return ''; }).replace(/\n{3,}/g, '\n\n').trim();
              fill(d, body);
              var first = d.querySelector('a:not(.mapcover)');
              photos.forEach(function (u) {
                var img = document.createElement('img');
                img.className = 'thumb'; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer'; img.alt = 'Foto del producto'; img.src = u;
                img.addEventListener('error', function () { img.remove(); });
                if (first) {
                  var a = document.createElement('a');
                  a.className = 'thumbwrap'; a.href = first.href; a.target = '_blank'; a.rel = 'noopener noreferrer';
                  a.setAttribute('aria-label', 'Ver producto'); a.appendChild(img);
                  d.appendChild(a);
                } else d.appendChild(img);
              });
            } else d.textContent = text;
            log.appendChild(d);
            log.scrollTop = log.scrollHeight;
            return d;
          }

          async function call(path, body) {
            var init = body === undefined ? {} : { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) };
            try {
              var r = await fetch('/chat/v1' + path, init);
              var j = null;
              try { j = await r.json(); } catch (e) { /* sin cuerpo */ }
              return { ok: r.ok, status: r.status, body: j };
            } catch (e) {
              return { ok: false, status: 0, body: null };
            }
          }

          // Textos que la agencia personalizó; los que faltan quedan con el valor por defecto de la página. Todo con textContent / placeholder.
          function t(key, fallback) { return typeof texts[key] === 'string' && texts[key] ? texts[key] : fallback; }

          function applyBrand(b) {
            texts = b.texts && typeof b.texts === 'object' ? b.texts : {};
            if (/^(https:\/\/|\/chat\/v1\/bt_[\w-]+\/icon\?v=\d+$)/i.test(b.logo_url || '')) { $('logo').src = b.logo_url; $('logo').hidden = false; }
            input.placeholder = t('placeholder', input.placeholder);
            send.textContent = t('send', send.textContent);
            $('openLead').textContent = t('lead_link', $('openLead').textContent);
            $('lName').placeholder = t('lead_name', $('lName').placeholder);
            $('lEmail').placeholder = t('lead_email', $('lEmail').placeholder);
            $('lPhone').placeholder = t('lead_phone', $('lPhone').placeholder);
            $('lInterest').placeholder = t('lead_interest', $('lInterest').placeholder);
            $('consentText').textContent = t('consent', $('consentText').textContent);
          }

          function errorText(r, fallback) {
            if (r.status === 0) return 'Sin conexión. Revisa tu internet e intenta de nuevo.';
            return r.body && r.body.error && r.body.error.message ? r.body.error.message : fallback;
          }

          function stop(message) {
            ended = true;
            input.disabled = true;
            send.disabled = true;
            if (message) add('note', message);
          }

          async function open() {
            var r = await call('/sessions', { key: KEY });
            var loading = $('loading');
            if (loading) loading.remove();
            if (!r.ok) { stop(errorText(r, 'Este chat no está disponible.')); return false; }
            token = r.body.token;
            if (!$('title').dataset.ready) {
              $('title').textContent = r.body.name;
              $('title').dataset.ready = '1';
              if (/^#[0-9a-fA-F]{6}$/.test(r.body.color || '')) document.documentElement.style.setProperty('--c', r.body.color);
              if (/^https?:\/\//i.test(r.body.privacy_url || '')) privacyUrl = r.body.privacy_url;
              applyBrand(r.body);
              add('bot', r.body.welcome);
            }
            return true;
          }

          function showLeadLink() { if (!leadSent) $('more').style.display = 'block'; }

          // Cuando ya hay conversación, se pregunta cada pocos segundos si una persona del negocio escribió (solo con la pestaña visible).
          function schedulePoll() {
            if (pollTimer || ended || !token) return;
            pollTimer = setTimeout(async function () {
              pollTimer = null;
              if (ended || !token) return;
              if (document.visibilityState === 'visible' && !busy) {
                var r = await call('/poll', { token: token, after: lastAgentAt });
                if (r.status === 401) { token = null; return; }
                if (r.ok && r.body) {
                  (r.body.messages || []).forEach(function (m) {
                    add('agent', m.text);
                    if (!lastAgentAt || m.at > lastAgentAt) lastAgentAt = m.at;
                  });
                  if (r.body.with_person) markWithPerson();
                }
              }
              schedulePoll();
            }, withPerson ? 4000 : 8000);
          }

          function markWithPerson() {
            withPerson = true;
            if (!announcedPerson) { announcedPerson = true; add('note', t('with_person', 'Una persona del equipo está atendiendo esta conversación.')); }
          }

          async function submit(text) {
            if (!token && !(await open())) return;
            var r = await call('/messages', { token: token, text: text });
            if (r.status === 401 && (await open())) r = await call('/messages', { token: token, text: text });
            return r;
          }

          $('form').addEventListener('submit', async function (e) {
            e.preventDefault();
            var text = input.value.trim();
            if (!text || busy || ended) return;
            busy = true;
            send.disabled = true;
            input.value = '';
            add('user', text);
            var typing = add('note', t('typing', 'Escribiendo…'));
            var r = await submit(text);
            typing.remove();
            if (r && r.ok) {
              if (r.body.reply) add('bot', r.body.reply);
              if (r.body.with_person) markWithPerson();
              schedulePoll();
              if (r.body.offer_lead) showLeadLink();
              if (r.body.remaining_messages <= 0) { stop('Llegaste al límite de mensajes de esta conversación.'); showLeadLink(); }
            } else if (r) {
              add('note', errorText(r, 'No pudimos responder. Intenta de nuevo.'));
              if (r.status === 429) showLeadLink();
            }
            busy = false;
            if (!ended) { send.disabled = false; input.focus(); }
          });

          $('openLead').addEventListener('click', function () {
            $('lead').style.display = 'flex';
            $('form').style.display = 'none';
            $('more').style.display = 'none';
            var consent = $('consentText');
            if (privacyUrl && !consent.dataset.linked) {
              consent.dataset.linked = '1';
              consent.appendChild(document.createTextNode(' Ver '));
              var a = document.createElement('a');
              a.href = privacyUrl; a.target = '_blank'; a.rel = 'noopener noreferrer';
              a.textContent = 'política de privacidad';
              consent.appendChild(a);
              consent.appendChild(document.createTextNode('.'));
            }
          });

          function closeLead() {
            $('lead').style.display = 'none';
            if (!ended) $('form').style.display = 'flex';
            showLeadLink();
          }
          $('leadCancel').addEventListener('click', closeLead);

          $('leadSend').addEventListener('click', async function () {
            $('leadErr').textContent = '';
            if (!token && !(await open())) return;
            var r = await call('/lead', {
              token: token, name: $('lName').value, email: $('lEmail').value, phone: $('lPhone').value,
              interest: $('lInterest').value, consent: $('lConsent').checked
            });
            if (r.ok) {
              leadSent = true;
              $('lead').style.display = 'none';
              $('more').style.display = 'none';
              if (!ended) $('form').style.display = 'flex';
              add('note', t('thanks', '¡Gracias! Recibimos tus datos y te contactaremos.'));
            } else {
              $('leadErr').textContent = errorText(r, 'No pudimos guardar tus datos. Intenta de nuevo.');
            }
          });

          open();
        })();
        </script>
        </body>
        </html>
        """;
}

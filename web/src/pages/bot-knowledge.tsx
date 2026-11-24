import { parseFaq } from '@/lib/faq-parse'
import { EMPTY_PRODUCT, isHttpsImage, parseProducts, type ProductRow } from '@/lib/product-parse'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon, SearchIcon } from 'lucide-react'
import { useEffect, useState } from 'react'
import { ConfirmDialog, EmptyState, FieldError, NativeSelect, formatDate, notifyError } from '@/components/common'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, del, get, post, postForm, put } from '@/lib/api'
import type { KnowledgeHit, KnowledgeSource } from '@/lib/types'

const KIND_LABEL: Record<string, string> = { products: 'Productos', faq: 'Preguntas frecuentes', text: 'Texto', pdf: 'PDF', url: 'Página web', supabase: 'Catálogo (Supabase)' }
const REFRESH_LABEL: Record<number, string> = { 15: '15 minutos', 60: 'hora', 360: '6 horas', 1440: 'día' }
/** Lo que se carga a mano se puede editar; lo que se lee de fuera (página, catálogo) se vuelve a leer. */
const EDITABLE = new Set(['faq', 'products', 'text', 'pdf'])
const STATUS_LABEL: Record<string, string> = { indexed: 'Listo', pending: 'Leyendo…', failed: 'Falló' }

/** Información con la que el chatbot responde: preguntas frecuentes, texto, PDF y páginas web. */
export default function KnowledgePanel({ botId }: { botId: string }) {
  const queryClient = useQueryClient()
  const [adding, setAdding] = useState(false)
  const [removing, setRemoving] = useState<KnowledgeSource | null>(null)
  const [editing, setEditing] = useState<KnowledgeSource | null>(null)

  const key = ['bot-sources', botId]
  const sources = useQuery({
    queryKey: key,
    queryFn: () => get<{ sources: KnowledgeSource[] }>(`/panel/bots/${botId}/sources/`),
    // Mientras haya páginas por leer se vuelve a preguntar hasta que el servidor termine.
    refetchInterval: (query) => (query.state.data?.sources.some((s) => s.status === 'pending') ? 4000 : false),
  })
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })

  const remove = useMutation({
    mutationFn: (id: string) => del(`/panel/bots/${botId}/sources/${id}`),
    onSuccess: () => {
      setRemoving(null)
      refresh()
    },
    onError: notifyError,
  })
  const reread = useMutation({
    mutationFn: (id: string) => post(`/panel/bots/${botId}/sources/${id}/reindex`),
    onSuccess: refresh,
    onError: notifyError,
  })

  const list = sources.data?.sources ?? []

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="max-w-xl text-sm text-muted-foreground">
          El chatbot solo responde con lo que cargues aquí. Mientras más claro y completo, mejores respuestas.
        </p>
        <Button onClick={() => setAdding(true)}>
          <PlusIcon /> Añadir información
        </Button>
      </div>

      {sources.isLoading ? (
        <Skeleton className="h-32 w-full" />
      ) : !list.length ? (
        <EmptyState>Todavía no hay información. Añade preguntas frecuentes, un texto, un PDF o la dirección de una página de tu web.</EmptyState>
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Fuente</TableHead>
                <TableHead>Estado</TableHead>
                <TableHead className="hidden text-right md:table-cell">Fragmentos</TableHead>
                <TableHead className="hidden lg:table-cell">Añadida</TableHead>
                <TableHead className="w-44" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.map((s) => (
                <TableRow key={s.id}>
                  <TableCell>
                    <div className="font-medium">{s.title}</div>
                    <div className="text-xs text-muted-foreground">{KIND_LABEL[s.kind] ?? s.kind}{s.source_url ? ` · ${s.source_url}` : ''}{s.table ? ` · ${s.table}` : ''}</div>
                    {s.kind === 'supabase' && (
                      <div className="text-xs text-muted-foreground">
                        {s.refresh_minutes ? `Se actualiza cada ${REFRESH_LABEL[s.refresh_minutes] ?? `${s.refresh_minutes} min`}` : 'Se actualiza solo a mano'}
                        {s.synced_at ? ` · última lectura ${formatDate(s.synced_at)}` : ''}
                      </div>
                    )}
                  </TableCell>
                  <TableCell>
                    <Badge variant={s.status === 'failed' ? 'destructive' : s.status === 'pending' ? 'outline' : 'secondary'}>{STATUS_LABEL[s.status] ?? s.status}</Badge>
                    {s.error && <div className="mt-1 text-xs text-destructive">{s.error}</div>}
                  </TableCell>
                  <TableCell className="hidden text-right tabular-nums md:table-cell">{s.chunks}</TableCell>
                  <TableCell className="hidden lg:table-cell">{formatDate(s.created_at)}</TableCell>
                  <TableCell className="text-right">
                    {(s.kind === 'url' || s.kind === 'supabase') && (
                      <Button variant="ghost" size="sm" disabled={reread.isPending || s.status === 'pending'} onClick={() => reread.mutate(s.id)} aria-label={`${s.kind === 'supabase' ? 'Actualizar' : 'Volver a leer'} ${s.title}`}>
                        {s.kind === 'supabase' ? 'Actualizar ahora' : 'Volver a leer'}
                      </Button>
                    )}
                    {EDITABLE.has(s.kind) && (
                      <Button variant="ghost" size="sm" onClick={() => setEditing(s)} aria-label={`Editar ${s.title}`}>
                        Editar
                      </Button>
                    )}
                    <Button variant="ghost" size="sm" onClick={() => setRemoving(s)} aria-label={`Eliminar ${s.title}`}>
                      Eliminar
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <SearchTest botId={botId} />

      <AddDialog
        botId={botId}
        open={adding}
        onOpenChange={setAdding}
        onAdded={() => {
          setAdding(false)
          refresh()
        }}
      />

      <EditDialog
        botId={botId}
        source={editing}
        onClose={() => setEditing(null)}
        onSaved={() => {
          setEditing(null)
          refresh()
        }}
      />

      <ConfirmDialog
        open={!!removing}
        onOpenChange={(o) => !o && setRemoving(null)}
        title="Eliminar información"
        description={`El chatbot dejará de usar "${removing?.title}". No se puede deshacer.`}
        confirmLabel="Eliminar"
        pending={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing.id)}
      />
    </div>
  )
}

interface SourceContent {
  id: string
  kind: string
  title: string
  text: string | null
  faq: { question: string; answer: string }[] | null
  products: ProductRow[] | null
  rebuilt: boolean
}

/** Edita una fuente cargada a mano: al guardar se reemplaza solo lo indexado de esa fuente. */
function EditDialog({ botId, source, onClose, onSaved }: { botId: string; source: KnowledgeSource | null; onClose: () => void; onSaved: () => void }) {
  const content = useQuery({
    queryKey: ['bot-source-content', botId, source?.id],
    queryFn: () => get<{ source: SourceContent }>(`/panel/bots/${botId}/sources/${source!.id}/content`),
    enabled: !!source,
    gcTime: 0,
  })

  return (
    <Dialog open={!!source} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Editar información</DialogTitle>
          <DialogDescription>Al guardar, el chatbot usa de inmediato la versión nueva y olvida la anterior.</DialogDescription>
        </DialogHeader>
        {content.isLoading ? <Skeleton className="h-40 w-full" /> : content.data && source ? <EditForm botId={botId} source={content.data.source} onSaved={onSaved} /> : <FieldError message={content.error ? 'No se pudo cargar esta información.' : undefined} />}
      </DialogContent>
    </Dialog>
  )
}

function EditForm({ botId, source, onSaved }: { botId: string; source: SourceContent; onSaved: () => void }) {
  const [title, setTitle] = useState(source.title)
  const [text, setText] = useState(source.text ?? '')
  const [items, setItems] = useState(source.faq?.length ? source.faq : [{ question: '', answer: '' }])
  const [products, setProducts] = useState<ProductRow[]>(
    source.products?.length ? source.products.map((p) => ({ ...EMPTY_PRODUCT, ...p, price: p.price ?? '', description: p.description ?? '', image_url: p.image_url ?? '', category: p.category ?? '' })) : [{ ...EMPTY_PRODUCT }],
  )
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setError(null)
  }, [title, text, items, products])

  const save = useMutation({
    mutationFn: () =>
      put(`/panel/bots/${botId}/sources/${source.id}`, {
        title,
        text: source.kind === 'text' || source.kind === 'pdf' ? text : undefined,
        faq: source.kind === 'faq' ? items : undefined,
        products: source.kind === 'products' ? products.filter((p) => p.name.trim() || p.price.trim() || p.description.trim() || p.image_url.trim()) : undefined,
      }),
    onSuccess: onSaved,
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo guardar.'),
  })

  return (
    <form
      className="space-y-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      {source.rebuilt && (
        <p className="rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm">
          Esta información se cargó antes de que se pudiera editar: se reconstruyó a partir de lo que el chatbot tiene guardado. Revísala completa antes de guardar.
        </p>
      )}
      <div className="space-y-1.5">
        <Label htmlFor="edit-title">Título</Label>
        <Input id="edit-title" value={title} onChange={(e) => setTitle(e.target.value)} />
      </div>

      {(source.kind === 'text' || source.kind === 'pdf') && (
        <div className="space-y-1.5">
          <Label htmlFor="edit-text">Texto</Label>
          <Textarea id="edit-text" rows={14} value={text} onChange={(e) => setText(e.target.value)} />
        </div>
      )}

      {source.kind === 'faq' && (
        <div className="max-h-[50vh] space-y-3 overflow-y-auto pr-1">
          {items.map((item, i) => (
            <div key={i} className="space-y-1.5 rounded-lg border p-3">
              <Label htmlFor={`edit-q-${i}`}>Pregunta {i + 1}</Label>
              <Input id={`edit-q-${i}`} value={item.question} onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, question: e.target.value } : x)))} />
              <Label htmlFor={`edit-a-${i}`}>Respuesta {i + 1}</Label>
              <Textarea id={`edit-a-${i}`} rows={2} value={item.answer} onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, answer: e.target.value } : x)))} />
              {items.length > 1 && (
                <Button type="button" variant="ghost" size="sm" onClick={() => setItems(items.filter((_, j) => j !== i))}>
                  Quitar esta pregunta
                </Button>
              )}
            </div>
          ))}
          <Button type="button" variant="outline" size="sm" onClick={() => setItems([...items, { question: '', answer: '' }])}>
            <PlusIcon /> Otra pregunta
          </Button>
        </div>
      )}

      {source.kind === 'products' && (
        <div className="max-h-[50vh] space-y-3 overflow-y-auto pr-1">
          {products.map((p, i) => {
            const set = (patch: Partial<ProductRow>) => setProducts(products.map((x, j) => (j === i ? { ...x, ...patch } : x)))
            const badImage = p.image_url.trim() !== '' && !isHttpsImage(p.image_url)
            return (
              <div key={i} className="grid gap-2 rounded-lg border p-3 sm:grid-cols-[1fr_9rem_9rem]">
                <div className="space-y-1">
                  <Label htmlFor={`edit-name-${i}`}>Producto {i + 1}</Label>
                  <Input id={`edit-name-${i}`} value={p.name} onChange={(e) => set({ name: e.target.value })} />
                </div>
                <div className="space-y-1">
                  <Label htmlFor={`edit-price-${i}`}>Precio</Label>
                  <Input id={`edit-price-${i}`} value={p.price} onChange={(e) => set({ price: e.target.value })} />
                </div>
                <div className="space-y-1">
                  <Label htmlFor={`edit-cat-${i}`}>Categoría</Label>
                  <Input id={`edit-cat-${i}`} value={p.category} onChange={(e) => set({ category: e.target.value })} />
                </div>
                <div className="space-y-1 sm:col-span-3">
                  <Label htmlFor={`edit-desc-${i}`}>Descripción</Label>
                  <Textarea id={`edit-desc-${i}`} rows={2} value={p.description} onChange={(e) => set({ description: e.target.value })} />
                </div>
                <div className="space-y-1 sm:col-span-3">
                  <Label htmlFor={`edit-img-${i}`}>Foto (dirección https)</Label>
                  <Input id={`edit-img-${i}`} type="url" value={p.image_url} onChange={(e) => set({ image_url: e.target.value })} aria-invalid={badImage} />
                  {badImage && <p className="text-xs text-destructive">Debe empezar por https:// y no tener espacios.</p>}
                </div>
                {products.length > 1 && (
                  <div className="sm:col-span-3">
                    <Button type="button" variant="ghost" size="sm" onClick={() => setProducts(products.filter((_, j) => j !== i))}>
                      Quitar este producto
                    </Button>
                  </div>
                )}
              </div>
            )
          })}
          <Button type="button" variant="outline" size="sm" onClick={() => setProducts([...products, { ...EMPTY_PRODUCT }])}>
            <PlusIcon /> Otro producto
          </Button>
        </div>
      )}

      <FieldError message={error ?? undefined} />
      <DialogFooter>
        <Button type="submit" disabled={save.isPending}>
          Guardar cambios
        </Button>
      </DialogFooter>
    </form>
  )
}

/** Muestra qué fragmentos encontraría el chatbot para una pregunta: sirve para saber si falta información. */
function SearchTest({ botId }: { botId: string }) {
  const [question, setQuestion] = useState('')
  const [asked, setAsked] = useState<string | null>(null)
  const search = useQuery({
    queryKey: ['bot-search', botId, asked],
    queryFn: () => get<{ hits: KnowledgeHit[] }>(`/panel/bots/${botId}/search?q=${encodeURIComponent(asked ?? '')}`),
    enabled: asked !== null,
  })

  return (
    <section className="space-y-2">
      <h2 className="text-sm font-medium">Probar qué encuentra el chatbot</h2>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          if (question.trim()) setAsked(question.trim())
        }}
      >
        <Input aria-label="Pregunta de prueba" value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="Escribe una pregunta como la haría un cliente" />
        <Button type="submit" variant="outline">
          <SearchIcon /> Buscar
        </Button>
      </form>
      {asked !== null &&
        (search.isLoading ? (
          <Skeleton className="h-16 w-full" />
        ) : !search.data?.hits.length ? (
          <p className="text-sm text-muted-foreground">No se encontró nada sobre eso: el chatbot diría que no lo sabe. Añade esa información.</p>
        ) : (
          <ul className="space-y-2">
            {search.data.hits.map((h, i) => (
              <li key={i} className="rounded-lg border p-3 text-sm">
                <p className="whitespace-pre-wrap">{h.text}</p>
                <p className="mt-1 text-xs text-muted-foreground">{h.source}</p>
              </li>
            ))}
          </ul>
        ))}
    </section>
  )
}

type Kind = 'faq' | 'products' | 'text' | 'pdf' | 'url' | 'supabase'

function AddDialog({ botId, open, onOpenChange, onAdded }: { botId: string; open: boolean; onOpenChange: (o: boolean) => void; onAdded: () => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Añadir información</DialogTitle>
          <DialogDescription>Elige de dónde sale lo que el chatbot debe saber.</DialogDescription>
        </DialogHeader>
        {open && <AddForm botId={botId} onAdded={onAdded} />}
      </DialogContent>
    </Dialog>
  )
}

function AddForm({ botId, onAdded }: { botId: string; onAdded: () => void }) {
  const [kind, setKind] = useState<Kind>('faq')
  const [title, setTitle] = useState('')
  const [text, setText] = useState('')
  const [url, setUrl] = useState('')
  const [table, setTable] = useState('')
  const [columns, setColumns] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [linkTemplate, setLinkTemplate] = useState('')
  const [imageColumn, setImageColumn] = useState('')
  const [refreshMinutes, setRefreshMinutes] = useState(60)
  const [file, setFile] = useState<File | null>(null)
  const [items, setItems] = useState([{ question: '', answer: '' }])
  const [bulk, setBulk] = useState('')
  const [bulkNote, setBulkNote] = useState<string | null>(null)
  const [products, setProducts] = useState<ProductRow[]>([{ ...EMPTY_PRODUCT }])
  const [productBulk, setProductBulk] = useState('')
  const [productNote, setProductNote] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const base = `/panel/bots/${botId}/sources`

  const add = useMutation({
    mutationFn: () => {
      if (kind === 'faq') return post(`${base}/faq`, { title, items })
      if (kind === 'products') return post(`${base}/products`, { title, items: products.filter((p) => p.name.trim() || p.price.trim() || p.description.trim() || p.image_url.trim()) })
      if (kind === 'text') return post(`${base}/text`, { title, text })
      if (kind === 'url') return post(`${base}/url`, { url, title })
      if (kind === 'supabase') return post(`${base}/supabase`, { url, title, table, columns, api_key: apiKey, refresh_minutes: refreshMinutes, link_template: linkTemplate, image_column: imageColumn })
      const form = new FormData()
      if (file) form.append('file', file)
      if (title.trim()) form.append('title', title.trim())
      return postForm(`${base}/pdf`, form)
    },
    onSuccess: onAdded,
    onError: (e) => setError(e instanceof ApiError ? e.message : 'No se pudo añadir la información.'),
  })

  return (
    <form
      className="space-y-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        add.mutate()
      }}
    >
      <div className="space-y-1.5">
        <Label htmlFor="src-kind">Tipo</Label>
        <NativeSelect id="src-kind" value={kind} onChange={(e) => setKind(e.target.value as Kind)}>
          <option value="faq">Preguntas frecuentes</option>
          <option value="products">Productos o platos (con precio y foto)</option>
          <option value="text">Texto</option>
          <option value="pdf">Archivo PDF</option>
          <option value="url">Página web</option>
          <option value="supabase">Catálogo (Supabase)</option>
        </NativeSelect>
      </div>

      <div className="space-y-1.5">
        <Label htmlFor="src-title">Título{kind === 'url' || kind === 'pdf' || kind === 'supabase' ? ' (opcional)' : ''}</Label>
        <Input id="src-title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder={kind === 'faq' ? 'Nombre de este grupo de preguntas' : kind === 'products' ? 'Nombre de este catálogo' : undefined} />
      </div>

      {kind === 'faq' && (
        <div className="space-y-3">
          <div className="space-y-1.5 rounded-lg border border-dashed p-3">
            <Label htmlFor="faq-bulk">Pegar varias preguntas de una vez</Label>
            <Textarea id="faq-bulk" rows={5} value={bulk} onChange={(e) => setBulk(e.target.value)} placeholder={'P: Pregunta que hace el cliente\nR: Respuesta que debe dar el asistente\n\nP: Otra pregunta\nR: Su respuesta'} />
            <div className="flex items-center gap-3">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  const parsed = parseFaq(bulk)
                  if (parsed.length === 0) {
                    setBulkNote('No encontré preguntas. Usa «P:» y «R:», o un título por pregunta.')
                    return
                  }
                  const current = items.filter((i) => i.question.trim() || i.answer.trim())
                  setItems([...current, ...parsed])
                  setBulk('')
                  setBulkNote(`Se agregaron ${parsed.length} preguntas. Revísalas abajo antes de guardar.`)
                }}
              >
                Agregar a la lista
              </Button>
              {bulkNote && <span className="text-xs text-muted-foreground">{bulkNote}</span>}
            </div>
          </div>
          {items.map((item, i) => (
            <div key={i} className="space-y-1.5 rounded-lg border p-3">
              <Label htmlFor={`faq-q-${i}`}>Pregunta {i + 1}</Label>
              <Input id={`faq-q-${i}`} value={item.question} onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, question: e.target.value } : x)))} />
              <Label htmlFor={`faq-a-${i}`}>Respuesta {i + 1}</Label>
              <Textarea id={`faq-a-${i}`} rows={2} value={item.answer} onChange={(e) => setItems(items.map((x, j) => (j === i ? { ...x, answer: e.target.value } : x)))} />
            </div>
          ))}
          <Button type="button" variant="outline" size="sm" onClick={() => setItems([...items, { question: '', answer: '' }])}>
            <PlusIcon /> Otra pregunta
          </Button>
          <p className="text-xs text-muted-foreground">Usa las palabras que usarían tus clientes: el chatbot busca por palabras, no por sinónimos.</p>
        </div>
      )}

      {kind === 'products' && (
        <div className="space-y-3">
          <div className="space-y-1.5 rounded-lg border border-dashed p-3">
            <Label htmlFor="prod-bulk">Pegar varios productos de una vez</Label>
            <Textarea
              id="prod-bulk"
              rows={4}
              value={productBulk}
              onChange={(e) => setProductBulk(e.target.value)}
              placeholder={'Nombre | Precio | Descripción | Enlace de la imagen | Categoría\nOtro producto | Precio'}
            />
            <p className="text-xs text-muted-foreground">Una línea por producto: nombre, precio, descripción y categoría, separados por «|» o por tabulador (puedes copiar las filas desde Excel o Google Sheets). La dirección de la foto puede ir en cualquier columna.</p>
            <div className="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  const parsed = parseProducts(productBulk)
                  if (parsed.length === 0) {
                    setProductNote('No encontré productos. Escribe una línea por producto: nombre | precio | descripción.')
                    return
                  }
                  setProducts([...products.filter((p) => p.name.trim() || p.price.trim() || p.description.trim() || p.image_url.trim()), ...parsed])
                  setProductBulk('')
                  setProductNote(`Se agregaron ${parsed.length} productos. Revísalos abajo antes de guardar.`)
                }}
              >
                Agregar a la lista
              </Button>
              {productNote && <span className="text-xs text-muted-foreground">{productNote}</span>}
            </div>
          </div>
          <div className="max-h-[45vh] space-y-3 overflow-y-auto pr-1">
            {products.map((p, i) => {
              const set = (patch: Partial<ProductRow>) => setProducts(products.map((x, j) => (j === i ? { ...x, ...patch } : x)))
              const badImage = p.image_url.trim() !== '' && !isHttpsImage(p.image_url)
              return (
                <div key={i} className="grid gap-2 rounded-lg border p-3 sm:grid-cols-[1fr_9rem_9rem]">
                  <div className="space-y-1">
                    <Label htmlFor={`prod-name-${i}`}>Producto {i + 1}</Label>
                    <Input id={`prod-name-${i}`} value={p.name} onChange={(e) => set({ name: e.target.value })} placeholder="Nombre del producto" />
                  </div>
                  <div className="space-y-1">
                    <Label htmlFor={`prod-price-${i}`}>Precio</Label>
                    <Input id={`prod-price-${i}`} value={p.price} onChange={(e) => set({ price: e.target.value })} placeholder="Precio, como quieras mostrarlo" />
                  </div>
                  <div className="space-y-1">
                    <Label htmlFor={`prod-cat-${i}`}>Categoría</Label>
                    <Input id={`prod-cat-${i}`} value={p.category} onChange={(e) => set({ category: e.target.value })} placeholder="Categoría (opcional)" />
                  </div>
                  <div className="space-y-1 sm:col-span-3">
                    <Label htmlFor={`prod-desc-${i}`}>Descripción</Label>
                    <Textarea id={`prod-desc-${i}`} rows={2} value={p.description} onChange={(e) => set({ description: e.target.value })} />
                  </div>
                  <div className="space-y-1 sm:col-span-3">
                    <Label htmlFor={`prod-img-${i}`}>Foto (dirección https)</Label>
                    <div className="flex items-center gap-2">
                      <Input id={`prod-img-${i}`} type="url" value={p.image_url} onChange={(e) => set({ image_url: e.target.value })} placeholder="Enlace de la imagen (opcional)" aria-invalid={badImage} />
                      {p.image_url.trim() !== '' && !badImage && <img src={p.image_url.trim()} alt="" className="size-10 shrink-0 rounded-md border object-cover" loading="lazy" onError={(e) => (e.currentTarget.style.display = 'none')} />}
                    </div>
                    {badImage && <p className="text-xs text-destructive">Debe empezar por https:// y no tener espacios.</p>}
                  </div>
                  {products.length > 1 && (
                    <div className="sm:col-span-3">
                      <Button type="button" variant="ghost" size="sm" onClick={() => setProducts(products.filter((_, j) => j !== i))}>
                        Quitar este producto
                      </Button>
                    </div>
                  )}
                </div>
              )
            })}
          </div>
          <Button type="button" variant="outline" size="sm" onClick={() => setProducts([...products, { ...EMPTY_PRODUCT }])}>
            <PlusIcon /> Otro producto
          </Button>
          <p className="text-xs text-muted-foreground">Cuando el cliente pida ver un plato, el asistente responde con su precio y descripción y le envía la foto (en WhatsApp llega como imagen). La foto debe estar en una dirección pública https.</p>
        </div>
      )}

      {kind === 'text' && (
        <div className="space-y-1.5">
          <Label htmlFor="src-text">Texto</Label>
          <Textarea id="src-text" rows={8} value={text} onChange={(e) => setText(e.target.value)} placeholder="Pega aquí el texto que el asistente debe conocer" />
        </div>
      )}

      {kind === 'url' && (
        <div className="space-y-1.5">
          <Label htmlFor="src-url">Dirección de la página</Label>
          <Input id="src-url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="Enlace de la página que quieres leer" />
          <p className="text-xs text-muted-foreground">Se lee una sola página (no todo el sitio) y puede tardar unos segundos.</p>
        </div>
      )}

      {kind === 'supabase' && (
        <div className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="sb-url">URL del proyecto</Label>
            <Input id="sb-url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="URL del proyecto de Supabase" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-table">Tabla o vista de productos</Label>
            <Input id="sb-table" value={table} onChange={(e) => setTable(e.target.value)} placeholder="Tabla que se va a leer" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-columns">Columnas (opcional)</Label>
            <Input id="sb-columns" value={columns} onChange={(e) => setColumns(e.target.value)} placeholder="Columnas a leer, separadas por coma" />
            <p className="text-xs text-muted-foreground">Vacío = todas, salvo las técnicas (id, created_at…). Elegirlas deja al chatbot solo con lo útil.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-link">Enlace de cada producto (opcional)</Label>
            <Input id="sb-link" value={linkTemplate} onChange={(e) => setLinkTemplate(e.target.value)} placeholder="Enlace de cada producto, con {columna} donde va su valor" />
            <p className="text-xs text-muted-foreground">Escribe entre llaves la columna que forma la dirección. Así el chatbot comparte el enlace cuando piden más información.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-image">Columna de la foto (opcional)</Label>
            <Input id="sb-image" value={imageColumn} onChange={(e) => setImageColumn(e.target.value)} placeholder="Columna con la imagen (opcional)" />
            <p className="text-xs text-muted-foreground">Puede ser una URL o una lista de URL (se usa la primera). El chat muestra la foto en miniatura y abre el producto al tocarla.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-key">Llave de solo lectura (anon / publishable)</Label>
            <Input id="sb-key" type="password" autoComplete="off" value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
            <p className="text-xs text-muted-foreground">Se guarda cifrada y no se vuelve a mostrar. Nunca uses la llave service_role.</p>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="sb-refresh">Actualizar</Label>
            <NativeSelect id="sb-refresh" value={String(refreshMinutes)} onChange={(e) => setRefreshMinutes(Number(e.target.value))}>
              <option value="0">Solo a mano</option>
              <option value="15">Cada 15 minutos</option>
              <option value="60">Cada hora</option>
              <option value="360">Cada 6 horas</option>
              <option value="1440">Cada día</option>
            </NativeSelect>
          </div>
        </div>
      )}

      {kind === 'pdf' && (
        <div className="space-y-1.5">
          <Label htmlFor="src-file">Archivo PDF</Label>
          <Input id="src-file" type="file" accept="application/pdf" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
          <p className="text-xs text-muted-foreground">Hasta 10 MB y 100 páginas. Debe tener texto (no un escaneo).</p>
        </div>
      )}

      <FieldError message={error ?? undefined} />
      <DialogFooter>
        <Button type="submit" disabled={add.isPending || (kind === 'pdf' && !file)}>
          Añadir
        </Button>
      </DialogFooter>
    </form>
  )
}

// Supabase Edge Function - AI Proxy for Trạm Sáng Tạo API
// Replaces ASP.NET Core Gateway with lightweight serverless function
// PLAN 106: API key stored server-side only, never exposed to Desktop App

import { serve } from "https://deno.land/std@0.168.0/http/server.ts"
import { createClient } from 'https://esm.sh/@supabase/supabase-js@2'

const TRAM_SANG_TAO_API_KEY = Deno.env.get('TRAM_SANG_TAO_API_KEY')!
const TRAM_SANG_TAO_ENDPOINT = 'https://tramsangtao.com/v1'
const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!
const SUPABASE_SERVICE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!

interface ImageGenerateRequest {
  prompt: string
  image?: string
  mask?: string
  width?: number
  height?: number
  num_outputs?: number
}

serve(async (req) => {
  if (req.method === 'OPTIONS') {
    return new Response(null, {
      headers: {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'POST, GET, OPTIONS',
        'Access-Control-Allow-Headers': 'authorization, x-client-info, apikey, content-type',
      },
    })
  }

  try {
    const authHeader = req.headers.get('Authorization')
    if (!authHeader) {
      return new Response(JSON.stringify({ error: 'Missing authorization' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      })
    }

    const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_KEY, {
      auth: {
        autoRefreshToken: false,
        persistSession: false
      }
    })

    const token = authHeader.replace('Bearer ', '')
    const { data: { user }, error: authError } = await supabase.auth.getUser(token)

    if (authError || !user) {
      return new Response(JSON.stringify({ error: 'Unauthorized', details: authError?.message }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      })
    }

    const url = new URL(req.url)
    const path = url.pathname.replace('/ai-proxy', '')

    if (path === '/generate' && req.method === 'POST') {
      const body = await req.json()
      const response = await fetch(`${TRAM_SANG_TAO_ENDPOINT}/image/generate`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${TRAM_SANG_TAO_API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(body),
      })
      const data = await response.json()
      return new Response(JSON.stringify(data), {
        status: response.status,
        headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
      })
    }

    if (path.startsWith('/jobs/') && req.method === 'GET') {
      const jobId = path.replace('/jobs/', '')
      const response = await fetch(`${TRAM_SANG_TAO_ENDPOINT}/jobs/${jobId}`, {
        method: 'GET',
        headers: { 'Authorization': `Bearer ${TRAM_SANG_TAO_API_KEY}` },
      })
      const data = await response.json()
      return new Response(JSON.stringify(data), {
        status: response.status,
        headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
      })
    }

    if (path === '/balance' && req.method === 'GET') {
      const response = await fetch(`${TRAM_SANG_TAO_ENDPOINT}/balance`, {
        method: 'GET',
        headers: { 'Authorization': `Bearer ${TRAM_SANG_TAO_API_KEY}` },
      })
      const data = await response.json()
      return new Response(JSON.stringify(data), {
        status: response.status,
        headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
      })
    }

    if (path === '/upscale' && req.method === 'POST') {
      const body = await req.json()
      const response = await fetch(`${TRAM_SANG_TAO_ENDPOINT}/upscale/image`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${TRAM_SANG_TAO_API_KEY}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(body),
      })
      const data = await response.json()
      return new Response(JSON.stringify(data), {
        status: response.status,
        headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
      })
    }

    return new Response(JSON.stringify({ error: 'Not found' }), {
      status: 404,
      headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
    })

  } catch (error) {
    console.error('Edge Function error:', error)
    return new Response(JSON.stringify({
      error: 'Internal server error',
      message: error instanceof Error ? error.message : 'Unknown error'
    }), {
      status: 500,
      headers: { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' },
    })
  }
})

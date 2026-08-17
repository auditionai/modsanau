// Create a real payment order for E2E testing
const SUPABASE_URL = 'https://plvuutsjwsawkkrmvigz.supabase.co';
const ANON_KEY = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InBsdnV1dHNqd3Nhd2trcm12aWd6Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY2MzEwOTgsImV4cCI6MjEwMjIwNzA5OH0.dl90yIqk2bvHOAsialb_KqlC1x3qZa84f0rOoP7hOFY';
const SERVICE_KEY = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InBsdnV1dHNqd3Nhd2trcm12aWd6Iiwicm9sZSI6InNlcnZpY2Vfcm9sZSIsImlhdCI6MTc4NjYzMTA5OCwiZXhwIjoyMTAyMjA3MDk4fQ.fL5AVbsQ2yrNFy98al1_QcGj1FVNdrMaWmKpPu6gsuY';

async function createOrder() {
  // Step 1: Create user via admin API with service role key
  const timestamp = Date.now();
  const testEmail = `e2e${timestamp}@test.local`;
  const testPassword = 'TestE2E123456!';

  console.log('🔐 Creating user via admin API:', testEmail);

  const createUserRes = await fetch(`${SUPABASE_URL}/auth/v1/admin/users`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${SERVICE_KEY}`,
      'apikey': SERVICE_KEY
    },
    body: JSON.stringify({
      email: testEmail,
      password: testPassword,
      email_confirm: true
    })
  });

  if (!createUserRes.ok) {
    console.log('❌ User creation failed:', createUserRes.status, await createUserRes.text());
    return;
  }

  console.log('✅ User created:', testEmail);

  // Step 2: Sign in with the created user
  const authRes = await fetch(`${SUPABASE_URL}/auth/v1/token?grant_type=password`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'apikey': ANON_KEY
    },
    body: JSON.stringify({
      email: testEmail,
      password: testPassword
    })
  });

  if (!authRes.ok) {
    console.log('❌ Auth failed:', authRes.status, await authRes.text());
    return;
  }

  const { access_token } = await authRes.json();
  console.log('✅ Signed in');

  // Step 2: Get subscription products
  console.log('\n📦 Getting products...');
  const productsRes = await fetch(`${SUPABASE_URL}/rest/v1/rpc/payment_api`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${access_token}`,
      'apikey': ANON_KEY
    },
    body: JSON.stringify({
      action: 'get_products',
      payload: {}
    })
  });

  const productsData = await productsRes.json();

  if (!productsRes.ok) {
    console.log('❌ Failed to get products:', productsData);
    return;
  }

  const products = Array.isArray(productsData) ? productsData : (productsData.data || []);
  const subscription30d = products.find(p => p.product_type === 'subscription' && p.credits_or_days === 30);

  if (!subscription30d) {
    console.log('❌ Subscription 30d not found');
    return;
  }

  console.log('✅ Found:', subscription30d.product_name, '- Price:', subscription30d.price_vnd, 'VND');

  // Step 3: Create order
  console.log('\n🛒 Creating order...');
  const orderRes = await fetch(`${SUPABASE_URL}/rest/v1/rpc/payment_api`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${access_token}`,
      'apikey': ANON_KEY
    },
    body: JSON.stringify({
      action: 'create_order',
      payload: {
        product_id: subscription30d.product_id
      }
    })
  });

  const order = await orderRes.json();

  if (!order.payment_code) {
    console.log('❌ Order creation failed:', order);
    return;
  }

  console.log('✅ Order created!');
  console.log('\n📋 PAYMENT DETAILS:');
  console.log('━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━');
  console.log('Payment Code:', order.payment_code);
  console.log('Amount:', order.amount_vnd, 'VND');
  console.log('Bank:', order.bank_code);
  console.log('Account:', order.bank_account_number);
  console.log('Account Name:', order.bank_account_name);
  console.log('Transfer Content:', order.transfer_content);
  console.log('QR Code URL:', order.qr_code_url);
  console.log('━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━');
  console.log('\n🏦 NEXT STEPS:');
  console.log('1. Open banking app');
  console.log('2. Scan QR code or manually transfer');
  console.log('3. Use EXACT content:', order.transfer_content);
  console.log('4. Wait for webhook callback (~30s)');
  console.log('5. Check order status with:');
  console.log(`   curl -X POST ${SUPABASE_URL}/rest/v1/rpc/payment_api \\`);
  console.log(`     -H "Authorization: Bearer ${access_token.substring(0, 20)}..." \\`);
  console.log(`     -H "Content-Type: application/json" \\`);
  console.log(`     -H "apikey: ${ANON_KEY}" \\`);
  console.log(`     -d '{"action":"get_order","payload":{"payment_code":"${order.payment_code}"}}'`);
}

createOrder().catch(console.error);

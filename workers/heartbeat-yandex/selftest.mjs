const store = { token: process.env.TEST_TOKEN || "secret" };
let object = null;
let puts = 0;

globalThis.fetch = async (url, options = {}) => {
  const target = String(url);
  if (target.includes("169.254.169.254")) {
    return new Response(JSON.stringify({ access_token: "iam-test", expires_in: 3600 }), { status: 200 });
  }
  if (target.startsWith("https://storage.yandexcloud.net/")) {
    if ((options.method || "GET") === "GET") {
      if (object === null) return new Response("not found", { status: 404 });
      return new Response(JSON.stringify(object), { status: 200 });
    }
    object = JSON.parse(options.body);
    puts += 1;
    return new Response("", { status: 200 });
  }
  throw new Error("unexpected url " + target);
};

process.env.BUCKET = "craftunity-heartbeat";
process.env.READ_TOKEN = store.token;

const { handler } = await import("./index.js");

const results = [];
function check(name, condition, detail = "") {
  results.push({ name, ok: Boolean(condition), detail });
  console.log((condition ? "  OK  " : " FAIL ") + name + (condition ? "" : " — " + detail));
}

const post = (body) => ({
  httpMethod: "POST",
  path: "/heartbeat",
  body: JSON.stringify(body),
  queryStringParameters: null,
  isBase64Encoded: false,
});

let response = await handler(post({ instance_id: "abc", username: "Dan", version: "1.20.1", loader: "fabric" }));
check("heartbeat принят", response.statusCode === 200);
check("объект записан", puts === 1 && object && object.abc && object.abc.username === "Dan");

response = await handler({
  httpMethod: "GET",
  path: "/clients",
  body: "",
  queryStringParameters: { token: "wrong" },
});
check("неверный токен отклонён", response.statusCode === 403, String(response.statusCode));

response = await handler({
  httpMethod: "GET",
  path: "/clients",
  body: "",
  queryStringParameters: { token: store.token },
});
const payload = JSON.parse(response.body);
check("список отдан", response.statusCode === 200 && payload.count === 1, response.body);
check("кириллица и ник сохранены", payload.clients[0].username === "Dan");

response = await handler(post({ username: "без id" }));
check("запрос без instance_id отклонён", response.statusCode === 400);

response = await handler(post({ instance_id: "abc", version: "1.21" }));
const merged = object.abc;
check("повторный heartbeat сохраняет first_seen", merged.first_seen <= merged.last_seen);

const failed = results.filter((item) => !item.ok);
console.log("");
if (failed.length) {
  console.log("Провалено: " + failed.map((item) => item.name).join(", "));
  process.exit(1);
}
console.log("Все проверки функции приёмника пройдены");

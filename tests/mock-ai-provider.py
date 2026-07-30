import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


PLAN = {
    "title": "Kế hoạch kiểm thử AI",
    "executiveSummary": "Ưu tiên kiểm tra các Shipping Instructions trong tập dữ liệu demo.",
    "actions": [
        {
            "priority": 1,
            "action": "Kiểm tra mốc giao hàng",
            "reason": "Dữ liệu có các mốc ETD và ETA cần được đối chiếu.",
            "relatedShippingInstructionIds": [],
        }
    ],
    "risks": ["Đây là response từ mock provider."],
    "assumptions": ["Không dùng để đánh giá chất lượng model thật."],
}

FILTER = {
    "product": "12",
    "salesOrganization": "SG50",
    "businessScenarios": ["PDO"],
    "siStatus": None,
    "salesOffice": None,
    "plantCode": None,
    "siId": None,
    "customer": None,
    "oilSc": None,
    "oilSo": None,
    "oilPo": None,
    "search": None,
    "createdFrom": "2026-01-01",
    "createdTo": "2026-12-31",
    "sortBy": "createdDate",
    "sortDirection": "desc",
    "summary": "Lọc Product 12, Sales Organization SG50 và Business Scenario PDO.",
    "assumptions": ["Khoảng ngày dùng cho kiểm thử mock."],
}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8")
        if self.path != "/openai/v1/chat/completions":
            self.send_error(404)
            return
        request = json.loads(body)
        system_content = request["messages"][0]["content"]
        user_content = json.loads(request["messages"][1]["content"])
        user_text = json.dumps(user_content, ensure_ascii=False)

        if "__provider_rate_limit__" in user_text:
            self.send_error(429, "Mock rate limit")
            return
        if "__delay__" in user_text:
            time.sleep(7)

        is_filter_request = "AI_FILTER_SCHEMA_V1" in system_content
        if is_filter_request:
            content = dict(FILTER)
            if "__unknown_field__" in user_text:
                content["sql"] = "DROP TABLE SapData"
        else:
            content = PLAN

        if "__invalid_json__" in user_text:
            content = "not-json"

        forbidden_keys = {"customername", "customer name"}
        if not is_filter_request and any(
            str(key).replace("_", "").lower() in forbidden_keys
            for record in user_content.get("records", [])
            for key in record
        ):
            self.send_error(422, "Sensitive Customer Name was included in the AI records")
            return

        response = {
            "choices": [
                {"message": {
                    "role": "assistant",
                    "content": content if isinstance(content, str) else json.dumps(content, ensure_ascii=False),
                }}
            ]
        }
        payload = json.dumps(response, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_):
        return


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8765), Handler).serve_forever()

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AppConfig } from './config';

export type Decision = 'APPROVE' | 'HOLD' | 'ESCALATE';

export interface AlertSummary {
  id: number;
  reason: string;
  status: string;
  createdAt: string;
  amount: number;
  currency: string;
  country: string;
  channel: string;
  merchant: string;
  customer: string;
  expectedDecision: Decision | null;
  latestDecision: Decision | null;
}

export interface AgentStep {
  stepNo: number;
  kind: 'ToolCall' | 'ToolResult' | 'ModelText';
  name: string;
  payloadJson: string;
}

export interface AgentRun {
  id: number;
  alertId: number;
  model: string;
  decision: Decision;
  confidence: number;
  reasons: string[];
  policyRules: string[];
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
  startedAt: string;
  durationMs: number;
  finalDecision: Decision | null;
  overriddenBy: string | null;
  steps: AgentStep[];
}

export interface AlertDetail {
  id: number;
  reason: string;
  status: string;
  createdAt: string;
  expectedDecision: Decision | null;
  transaction: {
    id: number;
    amount: number;
    currency: string;
    channel: string;
    country: string;
    occurredAt: string;
    merchant: string;
    customer: string;
  };
  runs: AgentRun[];
}

export interface Stats {
  runs: number;
  totalCostUsd: number;
  inputTokens: number;
  outputTokens: number;
  analystAgreementRate: number | null;
  labelledAlertsReviewed: number;
  accuracyVsExpected: number | null;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private cfg = inject(AppConfig);
  private get base() {
    return this.cfg.apiBaseUrl;
  }

  alerts(status?: string): Observable<AlertSummary[]> {
    return this.http.get<AlertSummary[]>(`${this.base}/alerts`, { params: status ? { status } : {} });
  }
  alert(id: number): Observable<AlertDetail> {
    return this.http.get<AlertDetail>(`${this.base}/alerts/${id}`);
  }
  review(id: number): Observable<AgentRun> {
    return this.http.post<AgentRun>(`${this.base}/alerts/${id}/review`, {});
  }
  finalize(runId: number, decision: Decision, analyst = 'analyst'): Observable<AgentRun> {
    return this.http.post<AgentRun>(`${this.base}/runs/${runId}/final`, { decision, analyst });
  }
  resetDemo(): Observable<void> {
    return this.http.post<void>(`${this.base}/demo/reset`, {});
  }
  stats(): Observable<Stats> {
    return this.http.get<Stats>(`${this.base}/stats`);
  }
}

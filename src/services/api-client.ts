import axios, { AxiosRequestConfig } from 'axios';

export interface FetchResponse<T> {
  count: number;
  next: string | null;
  results: T[];
}

export const axiosInstance = axios.create({
  baseURL: 'https://example.com/api',
});

class APIClient<T> {
  endpoint: string;

  constructor(endpoint: string) {
    this.endpoint = endpoint;
  }

  getAll = (config: AxiosRequestConfig) => {
    return axios.get<FetchResponse<T>>(this.endpoint, config).then((res) => res.data);
  };

  get = (id: number | string) => {
    return axios.get<T>(this.endpoint + '/' + id).then((res) => res.data);
  };
}

export default APIClient;
